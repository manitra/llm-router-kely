using System.Globalization;
using System.Text;
using RouterKely.Core.Identity;
using RouterKely.Core.Statistics;

namespace RouterKely.Ui;

/// <summary>
/// Renders the system usage page from retained daily aggregates: a handful of headline totals, then
/// one row per user over the days that actually saw traffic. Plain server-rendered HTML, so it
/// works without JavaScript and needs no stylesheet beyond the vendored Pico CSS.
/// </summary>
internal static class AdminUsagePage
{
    /// <summary>Days shown. The configured daily retention is at most 31, so this covers all of it.</summary>
    public const int WindowDays = 31;

    private const string DateFormat = "yyyy-MM-dd";
    private const string ColumnDateFormat = "MM-dd";

    public static string Render(
        UsageRowsSnapshot rows,
        IdentitySnapshot identities,
        DateOnly today,
        bool persisted)
    {
        DateOnly startDate = today.AddDays(-WindowDays + 1);
        DateOnly[] days = new DateOnly[WindowDays];
        var dayIndex = new Dictionary<DateOnly, int>(WindowDays);
        for (int offset = 0; offset < WindowDays; offset++)
        {
            days[offset] = startDate.AddDays(offset);
            dayIndex[days[offset]] = offset;
        }

        UserUsage[] users = Aggregate(rows, dayIndex, WindowDays);
        (long Requests, long Cost)[] columnTotals = ColumnTotals(users, WindowDays);

        // Trim leading days with no traffic so the table stays narrow on a fresh deployment.
        int firstUsed = Array.FindIndex(columnTotals, column => column.Requests > 0);
        if (firstUsed < 0)
            firstUsed = WindowDays - 1;

        var html = new StringBuilder(8_192);
        html.Append("<h1>Usage</h1>");
        html.Append("<p><small>")
            .Append(persisted
                ? "Daily usage is written to disk and restored after a restart."
                : "Daily usage is kept in memory only and is lost when the process restarts.")
            .Append(" Quota is enforced per process, so these figures are operational aggregates, not a billing ledger.</small></p>");

        long totalCost = users.Sum(user => user.Cost);
        long totalRequests = users.Sum(user => user.Requests);
        if (users.Length == 0)
        {
            html.Append("<p>No usage recorded in the last ")
                .Append(WindowDays.ToString(CultureInfo.InvariantCulture))
                .Append(" UTC days.</p>");
            return html.ToString();
        }

        WriteTotals(html, rows, days, firstUsed, users.Length, totalCost, totalRequests);
        WriteTable(html, users, identities, days, columnTotals, firstUsed, totalCost, totalRequests);
        return html.ToString();
    }

    private static void WriteTotals(
        StringBuilder html,
        UsageRowsSnapshot rows,
        DateOnly[] days,
        int firstUsed,
        int activeUsers,
        long totalCost,
        long totalRequests)
    {
        html.Append("<table><tbody><tr>")
            .Append("<th>Total spend</th><td><strong>").Append(FormatUsd(totalCost)).Append("</strong></td>")
            .Append("<th>Requests</th><td><strong>").Append(FormatCount(totalRequests)).Append("</strong></td>")
            .Append("</tr><tr>")
            .Append("<th>Prompt tokens</th><td><strong>").Append(FormatCount(rows.Rows.Sum(row => row.InputTokens))).Append("</strong></td>")
            .Append("<th>Completion tokens</th><td><strong>").Append(FormatCount(rows.Rows.Sum(row => row.OutputTokens))).Append("</strong></td>")
            .Append("</tr><tr>")
            .Append("<th>Active users</th><td><strong>").Append(FormatCount(activeUsers)).Append("</strong></td>")
            .Append("<th>Window</th><td><strong>").Append(FormatRange(days[firstUsed], days[^1])).Append("</strong></td>")
            .Append("</tr></tbody></table>");
    }

    private static void WriteTable(
        StringBuilder html,
        UserUsage[] users,
        IdentitySnapshot identities,
        DateOnly[] days,
        (long Requests, long Cost)[] columnTotals,
        int firstUsed,
        long totalCost,
        long totalRequests)
    {
        Dictionary<long, IdentityUser> byId = identities.Users.ToDictionary(user => user.Id);

        html.Append("<h2>By user</h2><table><thead><tr><th>User</th>");
        for (int index = firstUsed; index < days.Length; index++)
            html.Append("<th>").Append(days[index].ToString(ColumnDateFormat, CultureInfo.InvariantCulture)).Append("</th>");
        html.Append("<th>Total</th></tr></thead><tbody>");

        foreach (UserUsage user in users)
        {
            html.Append("<tr><td>");
            if (byId.TryGetValue(user.UserId, out IdentityUser? identity))
            {
                html.Append(AdminUiService.Encode(identity.Name))
                    .Append("<br><small>").Append(AdminUiService.Encode(identity.Email)).Append("</small>");
                if (!identity.Enabled)
                    html.Append(" <small>(disabled)</small>");
            }
            else
            {
                html.Append('#').Append(user.UserId.ToString(CultureInfo.InvariantCulture))
                    .Append(" <small>(removed)</small>");
            }

            html.Append("</td>");
            for (int index = firstUsed; index < days.Length; index++)
            {
                (long requests, long cost) = user.Cells[index];
                html.Append("<td>");
                if (requests == 0)
                {
                    html.Append("&middot;");
                }
                else
                {
                    html.Append(FormatCount(requests))
                        .Append("<br><small>").Append(FormatUsd(cost)).Append("</small>");
                }
                html.Append("</td>");
            }

            html.Append("<td><strong>").Append(FormatUsd(user.Cost)).Append("</strong><br><small>")
                .Append(FormatCount(user.Requests)).Append(" req</small></td></tr>");
        }

        html.Append("</tbody><tfoot><tr><td>All users</td>");
        for (int index = firstUsed; index < days.Length; index++)
        {
            html.Append("<td><strong>").Append(FormatUsd(columnTotals[index].Cost))
                .Append("</strong><br><small>").Append(FormatCount(columnTotals[index].Requests))
                .Append("</small></td>");
        }
        html.Append("<td><strong>").Append(FormatUsd(totalCost)).Append("</strong><br><small>")
            .Append(FormatCount(totalRequests)).Append(" req</small></td></tr></tfoot></table>");
    }

    private static UserUsage[] Aggregate(
        UsageRowsSnapshot rows,
        Dictionary<DateOnly, int> dayIndex,
        int windowDays)
    {
        var byUser = new Dictionary<long, UserUsage>();
        foreach (UsageRow row in rows.Rows)
        {
            if (!dayIndex.TryGetValue(row.Date, out int index))
                continue;

            if (!byUser.TryGetValue(row.UserId, out UserUsage? usage))
            {
                usage = new UserUsage(row.UserId, new (long Requests, long Cost)[windowDays]);
                byUser.Add(row.UserId, usage);
            }

            (long requests, long cost) = usage.Cells[index];
            usage.Cells[index] = (requests + row.RequestCount, cost + row.CostNanoUsd);
            usage.Requests += row.RequestCount;
            usage.Cost += row.CostNanoUsd;
        }

        return byUser.Values
            .OrderByDescending(user => user.Cost)
            .ThenBy(user => user.UserId)
            .ToArray();
    }

    private static (long Requests, long Cost)[] ColumnTotals(UserUsage[] users, int windowDays)
    {
        var totals = new (long Requests, long Cost)[windowDays];
        foreach (UserUsage user in users)
        {
            for (int index = 0; index < windowDays; index++)
            {
                (long requests, long cost) = user.Cells[index];
                totals[index] = (totals[index].Requests + requests, totals[index].Cost + cost);
            }
        }
        return totals;
    }

    private static string FormatCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatUsd(long nanoUsd) =>
        "$" + (nanoUsd / 1_000_000_000m).ToString("0.#####", CultureInfo.InvariantCulture);

    private static string FormatRange(DateOnly start, DateOnly end)
    {
        int days = end.DayNumber - start.DayNumber + 1;
        return start.ToString(DateFormat, CultureInfo.InvariantCulture)
            + " to "
            + end.ToString(DateFormat, CultureInfo.InvariantCulture)
            + " ("
            + days.ToString(CultureInfo.InvariantCulture)
            + (days == 1 ? " day)" : " days)");
    }

    private sealed class UserUsage(long userId, (long Requests, long Cost)[] cells)
    {
        public long UserId { get; } = userId;

        public (long Requests, long Cost)[] Cells { get; } = cells;

        public long Requests { get; set; }

        public long Cost { get; set; }
    }
}
