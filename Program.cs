namespace ColumnstoreAnalyzer;

public static class Program
{
    public static int Main(string[] args)
    {
        AnalyzerOptions opt;
        try
        {
            opt = ParseArgs(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Argument error: " + ex.Message);
            PrintUsage();
            return 1;
        }

        try
        {
            Run(opt);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex.Message);
            return 2;
        }
    }

    private static void Run(AnalyzerOptions opt)
    {
        var analyzer = new Analyzer(opt);

        Console.WriteLine($"Connecting to {opt.Server} / {opt.Database} ...");
        var (major, startTime, uptimeDays) = analyzer.GetServerInfo();
        Console.WriteLine($"SQL Server major version {major}, instance started {startTime:yyyy-MM-dd HH:mm} ({uptimeDays} days uptime)");
        if (uptimeDays < 7)
            Console.WriteLine("WARNING: <7 days uptime - index usage stats may not reflect the full workload cycle.");
        if (major < 13)
            Console.WriteLine("WARNING: pre-2016 SQL Server - columnstore feature set is limited on this version.");

        Console.WriteLine($"\nDiscovering tables (>= {opt.MinRowCount:N0} rows OR >= {opt.MinTableSizeMb:N0} MB, top {opt.TopNCandidates}) ...");
        var tables = analyzer.DiscoverTables();
        Console.WriteLine($"Found {tables.Count} candidate table(s).");
        if (tables.Count == 0) return;

        int n = 0;
        foreach (var t in tables)
        {
            n++;
            Console.WriteLine($"\n[{n}/{tables.Count}] {t.FullName}  ({t.RowCount:N0} rows, {t.TotalSizeMb:N0} MB)");

            Console.Write("  - operational stats ... ");
            analyzer.LoadOperationalStats(t);
            Console.WriteLine("ok");

            Console.Write("  - index inventory ... ");
            analyzer.LoadIndexes(t);
            Console.WriteLine($"{t.Indexes.Count} index(es)");

            Console.Write("  - column metadata ... ");
            analyzer.LoadColumns(t);
            Console.WriteLine($"{t.Columns.Count} column(s)");

            if (t.HasColumnstore)
            {
                Console.WriteLine("  - already has a columnstore index, skipping data sampling");
            }
            else
            {
                Console.Write("  - analyzing data cardinality" +
                              (t.RowCount > opt.SampleTargetRows * 2 ? $" (sampling ~{opt.SampleTargetRows:N0} rows)" : " (full scan)") + " ... ");
                try
                {
                    analyzer.AnalyzeColumnCardinality(t);
                    Console.WriteLine($"ok - {t.PctLowCardinalityColumns:0}% of columns <=1% distinct, " +
                                      $"median duplication factor {t.MedianDuplicationFactor:0.#}x");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAILED: " + ex.Message);
                }
            }
        }

        Console.WriteLine("\nScraping plan cache for referencing queries (one pass) ...");
        try
        {
            analyzer.MatchPlanCacheQueries(tables);
            Console.WriteLine($"Matched queries for {tables.Count(t => t.ReferencingQueries.Count > 0)} table(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Plan cache scrape failed (non-fatal): " + ex.Message);
        }

        foreach (var t in tables)
            Scoring.Score(t);

        ReportWriter.WriteAll(opt, tables);

        // Rule-based narrative (no AI needed)
        var narrative = Narrative.Build(opt, tables);
        File.WriteAllText(Path.Combine(opt.OutputFolder, "5_analysis.md"), narrative);
        Console.WriteLine();
        Console.WriteLine(new string('=', 100));
        Console.WriteLine(narrative);

        // Optional LLM narrative
        if (!string.IsNullOrWhiteSpace(opt.LlmEndpoint))
        {
            Console.WriteLine($"Requesting LLM narrative from {opt.LlmEndpoint} (model: {opt.LlmModel}) ...");
            try
            {
                var llmText = LlmClient.GenerateNarrative(opt, tables, narrative);
                File.WriteAllText(Path.Combine(opt.OutputFolder, "6_llm_analysis.md"), llmText);
                Console.WriteLine(new string('=', 100));
                Console.WriteLine(llmText);
            }
            catch (Exception ex)
            {
                Console.WriteLine("LLM narrative failed (non-fatal, rule-based analysis above still applies): " + ex.Message);
            }
        }
    }

    private static AnalyzerOptions ParseArgs(string[] args)
    {
        var opt = new AnalyzerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {args[i]}");
            switch (args[i].ToLowerInvariant())
            {
                case "--server": case "-s": opt.Server = Next(); break;
                case "--database": case "-d": opt.Database = Next(); break;
                case "--user": case "-u": opt.User = Next(); break;
                case "--password": case "-p": opt.Password = Next(); break;   // discouraged: lands in shell history
                case "--password-stdin": opt.PasswordFromStdin = true; break;
                case "--min-rows": opt.MinRowCount = long.Parse(Next()); break;
                case "--min-mb": opt.MinTableSizeMb = long.Parse(Next()); break;
                case "--top": opt.TopNCandidates = int.Parse(Next()); break;
                case "--sample-rows": opt.SampleTargetRows = long.Parse(Next()); break;
                case "--column-batch": opt.ColumnBatchSize = int.Parse(Next()); break;
                case "--timeout": opt.QueryTimeoutSeconds = int.Parse(Next()); break;
                case "--max-queries": opt.MaxQueriesPerTable = int.Parse(Next()); break;
                case "--output": case "-o": opt.OutputFolder = Next(); break;
                case "--no-trust-cert": opt.TrustServerCertificate = false; break;
                case "--llm-endpoint": opt.LlmEndpoint = Next(); break;
                case "--llm-model": opt.LlmModel = Next(); break;
                case "--llm-key": opt.LlmApiKey = Next(); break;
                case "--llm-timeout": opt.LlmTimeoutSeconds = int.Parse(Next()); break;
                case "--help": case "-h": PrintUsage(); Environment.Exit(0); break;
                default: throw new ArgumentException($"unknown argument: {args[i]}");
            }
        }
        if (string.IsNullOrWhiteSpace(opt.Server)) throw new ArgumentException("--server is required");
        if (string.IsNullOrWhiteSpace(opt.Database)) throw new ArgumentException("--database is required");
        
        // SQL auth with no password on the command line: get it safely.
        if (!string.IsNullOrEmpty(opt.User) && string.IsNullOrEmpty(opt.Password))
        {
            opt.Password = opt.PasswordFromStdin || Console.IsInputRedirected
                ? (Console.In.ReadLine() ?? "").TrimEnd('\r')      // piped: read first line of stdin
                : PasswordInput.Read($"Password for {opt.User}: "); // interactive: masked prompt
        }
        return opt;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"
ColumnstoreAnalyzer - finds tables whose DATA (lots of duplicates) and WORKLOAD make
them good candidates for columnstore indexes, replacing wide nonclustered B-trees.

Usage:
  ColumnstoreAnalyzer --server SQLPROD01 --database SalesDW [options]

Auth:
  (default)                 Windows integrated security
  --user X                  SQL auth - prompts for password interactively (masked)
  --user X --password-stdin SQL auth - reads password from stdin, for scripts:
                              echo $SQL_PW | ColumnstoreAnalyzer --server .. --user x --password-stdin
                              ColumnstoreAnalyzer --server .. --user x --password-stdin < pw.txt
  --user X --password Y     Discouraged: password ends up in shell history / process lists

Options:
  --min-rows N        Minimum row count to consider a table   (default 1,000,000)
  --min-mb N          ...or minimum size in MB                (default 500)
  --top N             Max tables to fully analyze             (default 25)
  --sample-rows N     Target sample size for data analysis    (default 1,000,000)
  --column-batch N    Columns per cardinality query           (default 20)
  --timeout N         Query timeout in seconds                (default 600)
  --max-queries N     Cached queries kept per table           (default 20)
  --output PATH       Report folder (default: ./columnstore_report_<timestamp>)
  --no-trust-cert     Do not set TrustServerCertificate=true

Narrative output (5_analysis.md) is generated by built-in rules - no AI required.
Optionally, add a second opinion from any OpenAI-compatible local LLM:
  --llm-endpoint URL  e.g. http://localhost:11434 (Ollama), http://localhost:1234 (LM Studio)
  --llm-model NAME    Model name at the endpoint (default: llama3.1)
  --llm-key KEY       Bearer token, if the endpoint needs one
  --llm-timeout N     LLM request timeout in seconds (default 300)

Requires VIEW SERVER STATE + read access on the target database.");
    }
}
