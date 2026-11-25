using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoTools.Automation
{
    public class JobRecord
    {
        public string Id { get; set; }
        public string Operation { get; set; }
        public string Status { get; set; }
        public string InputPath { get; set; }
        public string OutputPath { get; set; }
        public string Error { get; set; }
        public string ErrorCode { get; set; }
        public double Progress { get; set; }
        public double ElapsedSeconds { get; set; }
        public string CreatedAt { get; set; }
        public string StartedAt { get; set; }
        public string FinishedAt { get; set; }
        public int RunnerPid { get; set; }
        public int FfmpegPid { get; set; }
        public Dictionary<string, object> Request { get; set; }
        public Dictionary<string, object> Result { get; set; }

        public bool IsFinished
        {
            get { return Status == "completed" || Status == "failed" || Status == "cancelled"; }
        }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "id", Id },
                { "operation", Operation },
                { "status", Status },
                { "inputPath", InputPath },
                { "outputPath", OutputPath },
                { "error", Error },
                { "errorCode", ErrorCode },
                { "progress", Math.Round(Progress, 1) },
                { "elapsedSeconds", ElapsedSeconds },
                { "createdAt", CreatedAt },
                { "startedAt", StartedAt },
                { "finishedAt", FinishedAt },
                { "runnerPid", RunnerPid },
                { "ffmpegPid", FfmpegPid },
                { "request", Request }
            };
        }

        public static JobRecord FromDictionary(IDictionary<string, object> data)
        {
            return new JobRecord
            {
                Id = Json.Str(data, "id"),
                Operation = Json.Str(data, "operation"),
                Status = Json.Str(data, "status"),
                InputPath = Json.Str(data, "inputPath"),
                OutputPath = Json.Str(data, "outputPath"),
                Error = Json.Str(data, "error"),
                ErrorCode = Json.Str(data, "errorCode"),
                Progress = Json.Dbl(data, "progress") ?? 0,
                ElapsedSeconds = Json.Dbl(data, "elapsedSeconds") ?? 0,
                CreatedAt = Json.Str(data, "createdAt"),
                StartedAt = Json.Str(data, "startedAt"),
                FinishedAt = Json.Str(data, "finishedAt"),
                RunnerPid = Json.Int(data, "runnerPid") ?? 0,
                FfmpegPid = Json.Int(data, "ffmpegPid") ?? 0,
                Request = Json.AsObject(data.ContainsKey("request") ? data["request"] : null)
            };
        }
    }

    /// <summary>
    /// 持久化任务记录，支持异步执行、进度查询与取消。
    /// </summary>
    public static class JobManager
    {
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> ActiveJobs =
            new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);

        public static JobRecord Create(string operation, IDictionary<string, object> request)
        {
            var now = DateTime.Now;
            var job = new JobRecord
            {
                Id = "vt_" + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" +
                     Guid.NewGuid().ToString("N").Substring(0, 6),
                Operation = operation,
                Status = "queued",
                InputPath = Json.Str(request, "filePath") ?? Json.Str(request, "input") ?? Json.Str(request, "file"),
                CreatedAt = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Request = new Dictionary<string, object>(request ?? new Dictionary<string, object>())
            };
            Save(job);
            return job;
        }

        public static void Save(JobRecord job)
        {
            if (job == null || string.IsNullOrEmpty(job.Id)) return;
            AppPaths.EnsureDirectory(AppPaths.JobsDirectory);
            var path = Path.Combine(AppPaths.JobsDirectory, job.Id + ".json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, Json.Serialize(job.ToDictionary()));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static JobRecord Load(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw ToolException.InvalidArgument("缺少任务 ID。");
            var path = Path.Combine(AppPaths.JobsDirectory, id.Trim() + ".json");
            if (!File.Exists(path)) throw new ToolException("JOB_NOT_FOUND", "任务不存在：" + id);
            return JobRecord.FromDictionary(Json.ParseObject(File.ReadAllText(path)));
        }

        public static List<JobRecord> List(int limit = 20)
        {
            var jobs = new List<JobRecord>();
            if (!Directory.Exists(AppPaths.JobsDirectory)) return jobs;
            foreach (var file in Directory.GetFiles(AppPaths.JobsDirectory, "*.json"))
            {
                try
                {
                    jobs.Add(JobRecord.FromDictionary(Json.ParseObject(File.ReadAllText(file))));
                }
                catch (Exception)
                {
                }
            }
            return jobs.OrderByDescending(j => j.CreatedAt, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
        }

        public static JobRecord Run(JobRecord job, AutomationService service, CancellationToken externalToken = default(CancellationToken))
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            ActiveJobs[job.Id] = cts;
            var lastSave = DateTime.MinValue;

            try
            {
                job.Status = "running";
                job.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                Save(job);

                Action<double, string> progress = (percent, speed) =>
                {
                    job.Progress = percent;
                    if ((DateTime.UtcNow - lastSave).TotalMilliseconds >= 1000)
                    {
                        lastSave = DateTime.UtcNow;
                        Save(job);
                    }
                };
                Action<int> ffmpegStarted = pid =>
                {
                    job.FfmpegPid = pid;
                    Save(job);
                };

                var result = service.Execute(job.Operation, job.Request, progress, cts.Token, ffmpegStarted);
                job.Status = "completed";
                job.Progress = 100;
                job.ElapsedSeconds = Json.Dbl(result, "elapsedSeconds") ?? 0;
                var nestedOutput = result.ContainsKey("outputPath") ? result["outputPath"] : null;
                if (nestedOutput != null) job.OutputPath = Convert.ToString(nestedOutput, CultureInfo.InvariantCulture);
                job.Result = result;
            }
            catch (OperationCanceledException)
            {
                job.Status = "cancelled";
                job.ErrorCode = "CANCELLED";
                job.Error = "任务已取消。";
            }
            catch (ToolException ex)
            {
                job.Status = ex.Code == "CANCELLED" ? "cancelled" : "failed";
                job.ErrorCode = ex.Code;
                job.Error = ex.Message;
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.ErrorCode = "UNEXPECTED_ERROR";
                job.Error = ex.Message;
            }
            finally
            {
                job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                Save(job);
                CancellationTokenSource removed;
                ActiveJobs.TryRemove(job.Id, out removed);
            }

            return job;
        }

        /// <summary>后台进程执行（CLI --async 使用），父进程退出不影响任务。</summary>
        public static JobRecord StartBackground(JobRecord job, string ffmpegOverride = null)
        {
            var exePath = Process.GetCurrentProcess().MainModule.FileName;
            var arguments = "run-job --id " + job.Id;
            if (!string.IsNullOrWhiteSpace(ffmpegOverride)) arguments += " --ffmpeg " + FfmpegLocator.Quote(ffmpegOverride);
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppPaths.ExeDirectory
            };
            var process = Process.Start(startInfo);
            if (process != null)
            {
                job.RunnerPid = process.Id;
                job.Status = "running";
                Save(job);
            }
            return job;
        }

        public static JobRecord Cancel(string id)
        {
            var job = Load(id);
            if (job.IsFinished) return job;

            job.Status = "cancelled";
            job.ErrorCode = "CANCELLED";
            job.Error = "任务已取消。";
            job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Save(job);

            CancellationTokenSource cts;
            if (ActiveJobs.TryGetValue(job.Id, out cts))
            {
                cts.Cancel();
            }

            KillProcess(job.FfmpegPid);
            if (job.RunnerPid > 0 && job.RunnerPid != Process.GetCurrentProcess().Id) KillProcess(job.RunnerPid);
            return job;
        }

        private static void KillProcess(int pid)
        {
            if (pid <= 0) return;
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>等待任务结束，返回是否在超时前完成。</summary>
        public static bool Wait(Task task, int timeoutMs)
        {
            return task.Wait(timeoutMs);
        }
    }
}
