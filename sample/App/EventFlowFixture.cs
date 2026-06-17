namespace SampleApp;

public class EventFlowExample
{
    private System.Action? _handler;

    // Explicit add/remove: both bodies touch _handler. Data flow must reach both bodies.
    public event System.Action CustomEvent
    {
        add { _handler += value; }
        remove { _handler -= value; }
    }
}
