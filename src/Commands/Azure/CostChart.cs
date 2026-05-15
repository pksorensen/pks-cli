using System.Globalization;
using System.Text;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Commands.Azure;

/// <summary>
/// ASCII bar chart of daily cost. Same visual style as <c>pks claude usage</c>.
/// </summary>
internal static class CostChart
{
    private static readonly string[] Blocks = [" ", "▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];
    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[2m";
    private const string Cyan = "\x1b[36m";

    public static void Render(IAnsiConsole console, IReadOnlyList<DailyCostPoint> points, string currency)
    {
        console.Write(new Rule($"[bold]Cost per day ({Markup.Escape(string.IsNullOrEmpty(currency) ? "—" : currency)})[/]").RuleStyle("dim"));
        if (points.Count == 0)
        {
            console.MarkupLine("[dim]  (no daily cost data in this window)[/]");
            return;
        }

        int winW = console.Profile.Width > 10 ? console.Profile.Width : TerminalWidth();
        int winH = TerminalHeight();
        int yAxisW = 10;
        int chartW = Math.Max(20, winW - yAxisW - 1);
        int chartH = Math.Max(10, Math.Min(20, winH - 12));

        int numBars = Math.Min(points.Count, chartW);
        var data = points.OrderBy(p => p.Date).TakeLast(numBars).ToList();

        int colW = Math.Max(1, chartW / Math.Max(1, data.Count));
        int gapW = colW >= 3 ? 1 : 0;
        if (gapW > 0)
            colW = Math.Max(1, (chartW - (data.Count - 1)) / Math.Max(1, data.Count));
        int totalBarW = data.Count * colW + Math.Max(0, data.Count - 1) * gapW;

        double rawMax = (double)data.Max(d => d.Cost);
        if (rawMax <= 0) rawMax = 1;
        double[] niceIncrements = [0.5, 1.0, 2.0, 5.0, 10.0, 20.0, 50.0, 100.0, 200.0, 500.0, 1000.0];
        double yIncrement = niceIncrements.FirstOrDefault(inc => rawMax / inc >= 3, niceIncrements[^1]);
        double yMax = Math.Ceiling(rawMax / yIncrement) * yIncrement;
        double range = Math.Max(yIncrement, yMax);
        double rowH = range / chartH;

        int yLabelEvery = Math.Max(1, chartH / 6);

        var output = new StringBuilder();

        for (int row = 0; row < chartH; row++)
        {
            double rowTop = yMax - row * rowH;
            double rowBot = rowTop - rowH;

            bool showLabel = row % yLabelEvery == 0 || row == chartH - 1;
            string costLabel = rowTop >= 100 ? rowTop.ToString("F0", CultureInfo.InvariantCulture)
                              : rowTop >= 10 ? rowTop.ToString("F1", CultureInfo.InvariantCulture)
                              : rowTop.ToString("F2", CultureInfo.InvariantCulture);
            string yLbl = showLabel ? $"{costLabel,8} " : "         ";
            char axChar = row == chartH - 1 ? '┼' : '│';

            output.Append(Dim).Append(yLbl).Append(axChar).Append(Reset);

            for (int i = 0; i < data.Count; i++)
            {
                var day = data[i];
                double cost = (double)day.Cost;

                string blockChar;
                if (cost >= rowTop) blockChar = "█";
                else if (cost > rowBot)
                {
                    double frac = (cost - rowBot) / rowH;
                    int idx = (int)Math.Round(frac * 8);
                    blockChar = Blocks[Math.Clamp(idx, 1, 8)];
                }
                else blockChar = " ";

                output.Append(Cyan);
                for (int w = 0; w < colW; w++) output.Append(blockChar);
                output.Append(Reset);
                if (gapW > 0 && i < data.Count - 1) output.Append(' ');
            }

            output.AppendLine();
        }

        // X axis
        output.Append(Dim).Append(new string(' ', yAxisW)).Append('└').Append(new string('─', totalBarW)).AppendLine(Reset);

        // Date labels along X axis
        int stride = colW + gapW;
        const int lblW = 5;
        int labelEvery = Math.Max(1, (int)Math.Ceiling((double)(lblW + 1) / stride));
        int[] steps = [1, 2, 3, 5, 7, 10, 14, 21, 30];
        int step = steps.FirstOrDefault(s => s >= labelEvery, 30);

        int lineLen = yAxisW + totalBarW + 2;
        var xLine = new char[lineLen];
        Array.Fill(xLine, ' ');

        void PlaceLabel(int barIndex, string lbl, bool clearAround = false)
        {
            int center = yAxisW + barIndex * stride + colW / 2;
            int xPos = center - lbl.Length / 2;
            xPos = Math.Clamp(xPos, 0, lineLen - lbl.Length);
            if (clearAround)
            {
                int from = Math.Max(0, xPos - 1);
                int to = Math.Min(lineLen, xPos + lbl.Length + 1);
                for (int c = from; c < to; c++) xLine[c] = ' ';
            }
            for (int c = 0; c < lbl.Length && xPos + c < lineLen; c++)
                xLine[xPos + c] = lbl[c];
        }

        for (int i = 0; i < data.Count - 1; i += step)
            PlaceLabel(i, data[i].Date.ToString("MM-dd"));
        if (data.Count > 0)
            PlaceLabel(data.Count - 1, data[^1].Date.ToString("MM-dd"), clearAround: true);

        output.Append(Dim).Append(new string(xLine)).AppendLine(Reset);

        // Footer summary
        decimal total = points.Sum(p => p.Cost);
        int activeDays = points.Count(p => p.Cost > 0);
        decimal avgPerDay = activeDays > 0 ? total / activeDays : 0m;
        var busiest = points.OrderByDescending(p => p.Cost).First();

        output.Append(new string(' ', yAxisW + 2));
        output.AppendLine(
            $"total {total.ToString("N2", CultureInfo.InvariantCulture)} {currency}  " +
            $"avg/active-day {avgPerDay.ToString("N2", CultureInfo.InvariantCulture)}  " +
            $"busiest {busiest.Date:yyyy-MM-dd} ({busiest.Cost.ToString("N2", CultureInfo.InvariantCulture)})");

        // Write raw to System.Console — Spectre's IAnsiConsole.Write would try to
        // wrap the line and miscount the embedded ANSI escape codes as visible
        // characters, splitting each chart row in half.
        Console.Write(output.ToString());
    }

    private static int TerminalWidth()
    {
        if (!Console.IsOutputRedirected && Console.WindowWidth > 10)
            return Console.WindowWidth;
        if (int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out int cols) && cols > 10)
            return cols;
        return 120;
    }

    private static int TerminalHeight()
    {
        if (!Console.IsOutputRedirected && Console.WindowHeight > 10)
            return Console.WindowHeight;
        if (int.TryParse(Environment.GetEnvironmentVariable("LINES"), out int lines) && lines > 10)
            return lines;
        return 30;
    }
}
