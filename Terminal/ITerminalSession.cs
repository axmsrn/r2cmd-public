namespace R2Cmd.Terminal;

public interface ITerminalSession : IDisposable
{
    event Action<char[], int>? Output;
    event Action? Exited;
    bool IsRunning { get; }
    void Write(string text);
    void Resize(int cols, int rows);
}
