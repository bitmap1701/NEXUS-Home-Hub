using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHome.HostService
{
    /// <summary>
    /// Named Pipe Server (NEXUSHome.Cockpit.v1) for zero-overhead local IPC control of the Cockpit & HostService.
    /// </summary>
    public sealed class CockpitPipeServer : IDisposable
    {
        public const string PipeName = "NEXUSHome.Cockpit.v1";

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
                        string response = await _commandHandler(command.Trim()).ConfigureAwait(false);
                        await writer.WriteLineAsync(response).ConfigureAwait(false);
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
