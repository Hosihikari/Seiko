namespace Seiko;

public sealed class SeikoException : Exception
{
    public SeikoException(string message) : base(message)
    {
    }

    public SeikoException(string message, Exception inner) : base(message, inner)
    {
    }
}
