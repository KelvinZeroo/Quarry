using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuarryApp.Models;

namespace QuarryApp.Core;

/// <summary>
/// Persistent state store for DownloadJob queue across application restarts.
/// Automatically journals jobs list, states, progress, and URLs into ~/.quarry_jobs.json.
/// </summary>
public static class JobStore
{
    private static readonly string JobsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".quarry_jobs.json"
    );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object FileLock = new();

    public static List<DownloadJob> LoadJobs()
    {
        lock (FileLock)
        {
            var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jobs.json");
            var path = File.Exists(localPath) ? localPath : JobsFilePath;

            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var list = JsonSerializer.Deserialize<List<DownloadJob>>(json, JsonOptions);
                    if (list != null)
                    {
                        foreach (var job in list)
                        {
                            // If it was running or asking when the app closed, set it to Stopped so the user can resume
                            if (job.State == JobState.Running || job.State == JobState.Asking)
                            {
                                job.State = JobState.Stopped;
                            }
                        }
                        return list;
                    }
                }
                catch { }
            }
            return new List<DownloadJob>();
        }
    }

    public static void SaveJobs(IEnumerable<DownloadJob> jobs)
    {
        lock (FileLock)
        {
            try
            {
                var snapshot = jobs.ToList();
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                var tempPath = JobsFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(JobsFilePath)) File.Delete(JobsFilePath);
                File.Move(tempPath, JobsFilePath);
            }
            catch { }
        }
    }
}
