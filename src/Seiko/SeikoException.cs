namespace Seiko;

public sealed class SeikoException : Exception
{
    public string? Code { get; }

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
