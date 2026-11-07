using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHome.HostService
{
    /// <summary>
    /// Named Pipe Server (NEXUS_Home_Cockpit_Pipe, Gate 2 decision 1) for local IPC control of the Cockpit &amp; HostService.
    /// Protocol: one JSON object per line (JSONL), e.g. {"cmd":"STATUS"} -> {"ok":true,"cmd":"STATUS","result":"..."}.
    /// Plain-text commands (TOGGLE, SHOW, HIDE, STATUS, SHUTDOWN) are still accepted and answered in plain text.
    /// </summary>
    public sealed class CockpitPipeServer : IDisposable
    {
        public const string PipeName = "NEXUS_Home_Cockpit_Pipe";

        private readonly Func<string, Task<string>> _commandHandler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _listenTask;

        public CockpitPipeServer(Func<string, Task<string>> commandHandler)
        {
            _commandHandler = commandHandler;
            _listenTask = Task.Run(ListenLoopAsync);
        }

        private async Task ListenLoopAsync()
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 4,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                    using var writer = new StreamWriter(server, utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

                    string? command = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(command))
                    {
                        string request = command.Trim();
                        bool isJson = request.StartsWith('{');
                        string cmd = isJson ? ParseJsonCommand(request) : request;

                        string response = cmd.Length == 0
                            ? "ERR:InvalidRequest (expected {\"cmd\":\"STATUS\"})"
                            : await _commandHandler(cmd).ConfigureAwait(false);

                        string output = isJson
                            ? new JsonObject
                            {
                                ["ok"] = !response.StartsWith("ERR", StringComparison.Ordinal),
                                ["cmd"] = cmd,
                                ["result"] = response
                            }.ToJsonString()
                            : response;
                        await writer.WriteLineAsync(output).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                }
            }
        }

        private static string ParseJsonCommand(string line)
        {
            try
            {
                JsonNode? node = JsonNode.Parse(line);
                string cmd = node?["cmd"]?.GetValue<string>()?.Trim() ?? string.Empty;
                string? text = node?["text"]?.GetValue<string>();
                // {"cmd":"COCKPIT","text":"..."} -> "COCKPIT ..." (TASK-04: run a Cockpit command in-process)
                return string.IsNullOrWhiteSpace(text) || cmd.Length == 0 ? cmd : $"{cmd} {text.Trim()}";
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listenTask.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // Ignore cancellation exceptions on shutdown
            }
            _cts.Dispose();
        }
    }
}
