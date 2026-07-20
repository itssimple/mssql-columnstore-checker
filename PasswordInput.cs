using System.Text;

namespace ColumnstoreAnalyzer;
internal static class PasswordInput
{
    /// <summary>Masked interactive prompt. Falls back to stdin when input is piped/redirected.</summary>
    public static string Read(string prompt)
    {
        // Piped input (echo $PW | tool ..., or tool ... < secret.txt): read one line, no prompt.
        if (Console.IsInputRedirected)
            return (Console.In.ReadLine() ?? "").TrimEnd('\r');

        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);   // never echoes the real character
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length <= 0) continue;
                sb.Length--; Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        return sb.ToString();
    }
}