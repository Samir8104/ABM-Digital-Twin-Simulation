
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor utility: Assets ? Simulation ? Import Course CSV
/// Reads mceniry_courses_fall_2026.csv (or any similarly-formatted file) and
/// writes a CourseData ScriptableObject to Assets/Resources/CourseData.asset.
///
/// Columns used (0-indexed):
///   13 – Days   14 – Start Time   15 – End Time   17 – Room   20 – Total Enrolled
/// </summary>
public static class CourseDataImporter
{
    private const string MenuPath = "Assets/Simulation/Import Course CSV";
    private const string OutputPath = "Assets/Resources/CourseData.asset";

    // Column indices (0-based) ------------------------------------------------
    private const int ColDays = 13;
    private const int ColStart = 14;
    private const int ColEnd = 15;
    private const int ColRoom = 17;
    private const int ColEnrolled = 20;

    [MenuItem(MenuPath)]
    public static void Import()
    {
        string csvPath = EditorUtility.OpenFilePanel("Select Course CSV", Application.dataPath, "csv");
        if (string.IsNullOrEmpty(csvPath)) return;

        CourseData asset = AssetDatabase.LoadAssetAtPath<CourseData>(OutputPath);
        if (asset == null)
        {
            asset = ScriptableObject.CreateInstance<CourseData>();
            string dir = Path.GetDirectoryName(OutputPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
            AssetDatabase.CreateAsset(asset, OutputPath);
        }

        asset.sections.Clear();

        // Parse the WHOLE file as CSV records, not as text lines.
        // File.ReadAllLines() breaks on every \n, including ones that are
        // inside a quoted field — this was fragmenting rows whenever a
        // field (title/notes) contained an embedded line break, producing
        // rows with too few columns.
        var rows = ParseCsv(File.ReadAllText(csvPath));
        int imported = 0, skipped = 0;

        // Skip header row (row 0)
        for (int i = 1; i < rows.Count; i++)
        {
            string[] cols = rows[i];
            if (cols.Length == 1 && string.IsNullOrWhiteSpace(cols[0])) continue; // blank row

            if (cols.Length <= ColEnrolled)
            {
                Debug.LogWarning($"[CourseDataImporter] Row {i} SKIPPED (too few columns: {cols.Length}). Raw: {string.Join("|", cols)}");
                skipped++;
                continue;
            }

            string room = cols[ColRoom].Trim();
            if (string.IsNullOrEmpty(room))
            {
                Debug.LogWarning($"[CourseDataImporter] Row {i} SKIPPED (empty room). Raw: {string.Join("|", cols)}");
                skipped++; continue;
            }

            string enrolledStr = cols[ColEnrolled].Trim();
            if (!int.TryParse(enrolledStr, out int enrolled) || enrolled <= 0)
            {
                Debug.LogWarning($"[CourseDataImporter] Row {i} SKIPPED (bad enrolled '{enrolledStr}'). Room={room}. Raw: {string.Join("|", cols)}");
                skipped++; continue;
            }

            int startMin = ParseTime(cols[ColStart].Trim());
            int endMin = ParseTime(cols[ColEnd].Trim());
            if (startMin < 0 || endMin < 0)
            {
                Debug.LogWarning($"[CourseDataImporter] Row {i} SKIPPED (bad time. start='{cols[ColStart]}' end='{cols[ColEnd]}'). Room={room}. Raw: {string.Join("|", cols)}");
                skipped++; continue;
            }

            CourseDays days = ParseDays(cols[ColDays].Trim());
            if (days == CourseDays.None)
            {
                Debug.LogWarning($"[CourseDataImporter] Row {i} SKIPPED (unparseable days '{cols[ColDays]}'). Room={room}. Raw: {string.Join("|", cols)}");
                skipped++; continue;
            }

            asset.sections.Add(new CourseSection
            {
                roomNumber = room,
                startMinute = startMin,
                endMinute = endMin,
                totalEnrolled = enrolled,
                meetingDays = days
            });
            imported++;
        }

        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[CourseDataImporter] Imported {imported} sections, skipped {skipped}. Asset at {OutputPath}");
        EditorUtility.DisplayDialog("Import Complete",
            $"Imported {imported} sections.\nSkipped {skipped} rows.\n\nAsset: {OutputPath}", "OK");
    }

    // ?? Helpers ??????????????????????????????????????????????????????????????

    /// <summary>Parses "10:45  AM" or "01:25  PM" ? minutes since midnight.</summary>
    private static int ParseTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return -1;

        // Normalise: collapse whitespace, upper-case
        raw = System.Text.RegularExpressions.Regex.Replace(raw.Trim().ToUpperInvariant(), @"\s+", " ");

        bool pm = raw.Contains("PM");
        raw = raw.Replace("AM", "").Replace("PM", "").Trim();

        string[] parts = raw.Split(':');
        if (parts.Length < 2) return -1;

        if (!int.TryParse(parts[0], out int h)) return -1;
        if (!int.TryParse(parts[1], out int m)) return -1;

        if (pm && h != 12) h += 12;
        if (!pm && h == 12) h = 0;

        return h * 60 + m;
    }

    /// <summary>Parses "Monday,Wednesday,Friday" (possibly quoted) ? CourseDays flags.</summary>
    private static CourseDays ParseDays(string raw)
    {
        // Strip surrounding quotes
        raw = raw.Trim('"', '\'');
        CourseDays result = CourseDays.None;

        if (raw.Contains("Monday")) result |= CourseDays.Monday;
        if (raw.Contains("Tuesday")) result |= CourseDays.Tuesday;
        if (raw.Contains("Wednesday")) result |= CourseDays.Wednesday;
        if (raw.Contains("Thursday")) result |= CourseDays.Thursday;
        if (raw.Contains("Friday")) result |= CourseDays.Friday;

        return result;
    }

    /// <summary>Naive CSV line splitter that respects double-quoted fields.</summary>
    private static string[] SplitCsvLine(string line)
    {
        var fields = new System.Collections.Generic.List<string>();
        bool inQuote = false;
        var current = new System.Text.StringBuilder();

        foreach (char c in line) // inspects every char from left -> right
        {
            if (c == '"') { inQuote = !inQuote; }
            else if (c == ',' && !inQuote) { fields.Add(current.ToString()); current.Clear(); }
            else { current.Append(c); }
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
    /// <summary>
    /// Parses an entire CSV file's text into rows of fields, correctly
    /// treating quoted fields as opaque — including embedded commas AND
    /// embedded newlines (\r\n or \n), and "" as an escaped literal quote.
    /// This is what File.ReadAllLines() + per-line splitting cannot do.
    /// </summary>
    private static System.Collections.Generic.List<string[]> ParseCsv(string text)
    {
        var rows = new System.Collections.Generic.List<string[]>();
        var fields = new System.Collections.Generic.List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuote = false;

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (inQuote)
            {
                if (c == '"')
                {
                    // Escaped quote ("") -> literal quote char
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        current.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuote = false;
                    i++;
                    continue;
                }
                current.Append(c); // includes \r, \n, commas — all literal inside quotes
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuote = true;
                    i++;
                    break;
                case ',':
                    fields.Add(current.ToString());
                    current.Clear();
                    i++;
                    break;
                case '\r':
                    i++; // ignore, \n (or end) handles the row break
                    break;
                case '\n':
                    fields.Add(current.ToString());
                    current.Clear();
                    rows.Add(fields.ToArray());
                    fields.Clear();
                    i++;
                    break;
                default:
                    current.Append(c);
                    i++;
                    break;
            }
        }

        // Final field/row if file doesn't end with a newline
        if (current.Length > 0 || fields.Count > 0)
        {
            fields.Add(current.ToString());
            rows.Add(fields.ToArray());
        }

        return rows;
    }
}
#endif