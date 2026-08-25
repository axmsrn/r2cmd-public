using System;
using System.IO;
using System.Text;
using System.Threading;
using Renci.SshNet;

namespace R2Cmd.Terminal;

public sealed class SshShellSession : ITerminalSession
{
    private readonly SshClient _client;
    private readonly ShellStream _shell;
    private readonly Thread _readThread;
    private volatile bool _disposed;

    public event Action<char[], int>? Output;
    public event Action? Exited;
    public bool IsRunning => !_disposed && _client.IsConnected;

    public SshShellSession(SshSession session, int cols, int rows)
    {
        AuthenticationMethod authMethod;

        if (session.AuthMethod == SshAuthMethod.PrivateKey)
        {
            var keyFile = string.IsNullOrEmpty(session.Passphrase)
                ? new PrivateKeyFile(session.PrivateKeyPath)
                : new PrivateKeyFile(session.PrivateKeyPath, session.Passphrase);
            authMethod = new PrivateKeyAuthenticationMethod(session.Username, keyFile);
        }
        else
        {
            authMethod = new PasswordAuthenticationMethod(session.Username, session.Password);
        }

        var connectionInfo = new ConnectionInfo(session.Host, session.Port, session.Username, authMethod)
        {
            Timeout = TimeSpan.FromSeconds(session.TimeoutSeconds)
        };

        _client = new SshClient(connectionInfo);
        _client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _client.Connect();

        _shell = _client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 800, 600, 65536);

        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "SSH shell reader" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        var chars = new char[4096];
        var decoder = Encoding.UTF8.GetDecoder();

        try
        {
            while (!_disposed)
            {
                // Host reboot / network drop: do not block forever on Read()
                if (!_client.IsConnected)
                    break;

                if (!_shell.DataAvailable)
                {
                    Thread.Sleep(20);
                    continue;
                }

                int read = _shell.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                int decoded = decoder.GetChars(buffer, 0, read, chars, 0);
                if (decoded > 0) Output?.Invoke(chars, decoded);
            }
        }
        catch (IOException) { }
        catch { }

        if (!_disposed) Exited?.Invoke();
    }

    public void Write(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        try { _shell.Write(text); _shell.Flush(); }
        catch (IOException) { }
        catch { }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || !_client.IsConnected) return;
        if (cols < 8 || rows < 2) return;

        try
        {
            // PROPER SSH RESIZE: We must send a "window-change" request out-of-band.
            // Writing escape sequences to stdin (like \x1b[8;{rows};{cols}t) just types
            // them into the shell, which breaks apps like Midnight Commander.

            var shellType = _shell.GetType();

            // Try to find the public method in modern SSH.NET versions
            var method = shellType.GetMethod("SendWindowChangeRequest")
                      ?? shellType.GetMethod("ChangeWindowSize");

            if (method != null)
            {
                method.Invoke(_shell, new object[] { (uint)cols, (uint)rows, (uint)0, (uint)0 });
            }
            else
            {
                // Fallback for older SSH.NET versions: access the internal _channel via reflection
                var channelField = shellType.GetField("_channel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (channelField != null)
                {
                    var channel = channelField.GetValue(_shell);
                    var channelMethod = channel?.GetType().GetMethod("SendWindowChangeRequest");

                    // Parameters: uint columns, uint rows, uint width, uint height
                    channelMethod?.Invoke(channel, new object[] { (uint)cols, (uint)rows, (uint)0, (uint)0 });
                }
            }
        }
        catch { /* connection lost or method not found */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shell?.Dispose(); } catch { }
        try { if (_client.IsConnected) _client.Disconnect(); _client.Dispose(); } catch { }
    }
}
