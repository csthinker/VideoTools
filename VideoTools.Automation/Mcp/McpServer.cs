using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoTools.Automation.Mcp
{
    /// <summary>
    /// 极简 MCP stdio 服务：换行分隔的 JSON-RPC 2.0，无需额外运行时。
    /// </summary>
    public class McpServer
    {
        private readonly TextReader _reader;
        private readonly TextWriter _writer;
        private readonly AutomationService _service;
        private readonly List<McpToolDefinition> _tools;
        private readonly object _writeLock = new object();
        private static readonly string[] SupportedProtocolVersions = { "2024-11-05", "2025-03-26", "2025-06-18" };

        public McpServer(TextReader reader, TextWriter writer, AutomationService service)
        {
            _reader = reader;
            _writer = writer;
            _service = service;
            _tools = ToolCatalog.Build(service);
        }

        public void Run()
        {
            string line;
            while ((line = _reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                HandleLine(line);
            }
        }

        private void HandleLine(string line)
        {
            object id = null;
            try
            {
                var request = Json.ParseObject(line);
                var hasId = request.ContainsKey("id") && request["id"] != null;
                id = hasId ? request["id"] : null;
                var method = Json.Str(request, "method");
                var parameters = Json.AsObject(request.ContainsKey("params") ? request["params"] : null)
                                 ?? new Dictionary<string, object>();

                switch (method)
                {
                    case "initialize":
                        WriteResult(id, new Dictionary<string, object>
                        {
                            { "protocolVersion", NegotiateVersion(Json.Str(parameters, "protocolVersion")) },
                            {
                                "capabilities", new Dictionary<string, object>
                                {
                                    { "tools", new Dictionary<string, object> { { "listChanged", false } } }
                                }
                            },
                            {
                                "serverInfo", new Dictionary<string, object>
                                {
                                    { "name", "videotools" },
                                    { "title", "VideoTools 自动化" },
                                    { "version", "1.0.0" }
                                }
                            },
                            {
                                "instructions", "VideoTools 视频处理工具的 MCP 服务。执行类工具默认同步等待，可用 wait=false 改为后台任务，再用 videotools_jobs_get 查询。"
                            }
                        });
                        break;
                    case "notifications/initialized":
                    case "notifications/cancelled":
                        break;
                    case "ping":
                        WriteResult(id, new Dictionary<string, object>());
                        break;
                    case "tools/list":
                        WriteResult(id, new Dictionary<string, object> { { "tools", BuildToolList() } });
                        break;
                    case "tools/call":
                        WriteResult(id, CallTool(parameters));
                        break;
                    case "resources/list":
                        WriteResult(id, new Dictionary<string, object> { { "resources", new List<object>() } });
                        break;
                    case "resources/templates/list":
                        WriteResult(id, new Dictionary<string, object> { { "resourceTemplates", new List<object>() } });
                        break;
                    case "prompts/list":
                        WriteResult(id, new Dictionary<string, object> { { "prompts", new List<object>() } });
                        break;
                    case "logging/setLevel":
                        WriteResult(id, new Dictionary<string, object>());
                        break;
                    default:
                        if (id != null) WriteError(id, -32601, "不支持的方法：" + method);
                        break;
                }
            }
            catch (ToolException ex)
            {
                if (id != null) WriteError(id, -32602, ex.Message, ex.Code);
            }
            catch (Exception ex)
            {
                if (id != null) WriteError(id, -32603, ex.Message, "INTERNAL_ERROR");
            }
        }

        private Dictionary<string, object> CallTool(Dictionary<string, object> parameters)
        {
            var name = Json.Str(parameters, "name");
            var args = Json.AsObject(parameters.ContainsKey("arguments") ? parameters["arguments"] : null)
                       ?? new Dictionary<string, object>();
            var tool = _tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

            try
            {
                if (tool == null) throw ToolException.InvalidArgument("未知工具：" + name + "。可调用 videotools_capabilities 查看可用工具。");

                Dictionary<string, object> payload;
                if (!string.IsNullOrEmpty(tool.Operation))
                {
                    payload = RunJobTool(tool, args);
                }
                else
                {
                    payload = tool.Invoke(args);
                }

                var text = Json.Serialize(payload);
                var ok = Json.Bool(payload, "ok") ?? true;
                return new Dictionary<string, object>
                {
                    { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } },
                    { "isError", !ok }
                };
            }
            catch (ToolException ex)
            {
                var text = Json.Serialize(new Dictionary<string, object>
                {
                    { "ok", false },
                    { "errorCode", ex.Code },
                    { "error", ex.Message }
                });
                return new Dictionary<string, object>
                {
                    { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } },
                    { "isError", true }
                };
            }
            catch (Exception ex)
            {
                var text = Json.Serialize(new Dictionary<string, object>
                {
                    { "ok", false },
                    { "errorCode", "UNEXPECTED_ERROR" },
                    { "error", ex.Message }
                });
                return new Dictionary<string, object>
                {
                    { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } },
                    { "isError", true }
                };
            }
        }

        private Dictionary<string, object> RunJobTool(McpToolDefinition tool, Dictionary<string, object> args)
        {
            var wait = Json.Bool(args, "wait") ?? true;
            var timeoutMs = Json.Int(args, "timeoutMs") ?? 300000;
            var request = new Dictionary<string, object>(args);
            request.Remove("wait");
            request.Remove("timeoutMs");

            var job = JobManager.Create(tool.Operation, request);
            var task = Task.Run(() => JobManager.Run(job, _service));

            if (!wait)
            {
                return RunningPayload(job);
            }

            if (!task.Wait(timeoutMs > 0 ? timeoutMs : 300000))
            {
                var current = JobManager.Load(job.Id);
                return RunningPayload(current);
            }

            var finished = task.Result;
            if (finished.Status == "completed")
            {
                var payload = finished.Result != null
                    ? new Dictionary<string, object>(finished.Result)
                    : new Dictionary<string, object>();
                payload["ok"] = true;
                payload["jobId"] = finished.Id;
                payload["status"] = finished.Status;
                if (!string.IsNullOrEmpty(finished.OutputPath)) payload["outputPath"] = finished.OutputPath;
                return payload;
            }

            throw new ToolException(finished.ErrorCode ?? "JOB_FAILED", finished.Error ?? "任务失败。");
        }

        private static Dictionary<string, object> RunningPayload(JobRecord job)
        {
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "jobId", job.Id },
                { "status", job.Status },
                { "progress", Math.Round(job.Progress, 1) },
                { "message", "任务已在后台执行，可用 videotools_jobs_get 查询进度。" }
            };
        }

        private List<object> BuildToolList()
        {
            var list = new List<object>();
            foreach (var tool in _tools)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "name", tool.Name },
                    { "description", tool.Description },
                    { "inputSchema", tool.InputSchema }
                });
            }
            return list;
        }

        private static string NegotiateVersion(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested) && SupportedProtocolVersions.Contains(requested)) return requested;
            return "2025-06-18";
        }

        private void WriteResult(object id, Dictionary<string, object> result)
        {
            if (id == null) return;
            var response = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            };
            Write(response);
        }

        private void WriteError(object id, int code, string message, string dataCode = null)
        {
            if (id == null) return;
            var error = new Dictionary<string, object>
            {
                { "code", code },
                { "message", message }
            };
            if (!string.IsNullOrEmpty(dataCode)) error["data"] = new Dictionary<string, object> { { "errorCode", dataCode } };
            var response = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", error }
            };
            Write(response);
        }

        private void Write(Dictionary<string, object> response)
        {
            var text = Json.Serialize(response);
            lock (_writeLock)
            {
                _writer.WriteLine(text);
                _writer.Flush();
            }
        }
    }
}
