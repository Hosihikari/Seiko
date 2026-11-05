namespace Seiko;

public sealed class SeikoException : Exception
{
    public string? Code { get; }

    // An actionable next step, shown after the message. It never changes the
    // exit code and never repairs anything by itself. HintKey is the
    // localisable form; Hint stays the English JSON text.
    public string? Hint { get; init; }

    public string? HintKey { get; init; }

    public SeikoException(string message) : base(message)
    {
    }

    public SeikoException(string message, Exception inner) : base(message, inner)
    {
    }

    public SeikoException(string message, string code) : base(message)
    {
        Code = code;
    }

    public SeikoException(string message, string code, Exception inner) : base(message, inner)
    {
        Code = code;
    }
}
