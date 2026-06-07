
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

        string[] lines = File.ReadAllLines(csvPath);
        int imported = 0, skipped = 0;

        // Skip header row (line 0)
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Simple CSV split — commas inside quoted strings are handled by the
            // quoted-field parser below.
            string[] cols = SplitCsvLine(line);

            if (cols.Length <= ColEnrolled)
            {
                skipped++;
                continue;
            }

            // ?? Room ?????????????????????????????????????????????????????????
            string room = cols[ColRoom].Trim();
            if (string.IsNullOrEmpty(room)) { skipped++; continue; }

            // ?? Enrolled ?????????????????????????????????????????????????????
            string enrolledStr = cols[ColEnrolled].Trim();
            if (!int.TryParse(enrolledStr, out int enrolled) || enrolled <= 0)
            { skipped++; continue; }

            // ?? Times ????????????????????????????????????????????????????????
            int startMin = ParseTime(cols[ColStart].Trim());
            int endMin = ParseTime(cols[ColEnd].Trim());
            if (startMin < 0 || endMin < 0) { skipped++; continue; }

            // ?? Days ?????????????????????????????????????????????????????????
            CourseDays days = ParseDays(cols[ColDays].Trim());
            if (days == CourseDays.None) { skipped++; continue; }

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

        foreach (char c in line)
        {
            if (c == '"') { inQuote = !inQuote; }
            else if (c == ',' && !inQuote) { fields.Add(current.ToString()); current.Clear(); }
            else { current.Append(c); }
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
#endif