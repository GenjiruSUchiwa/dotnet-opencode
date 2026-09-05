namespace OpenCode.Server.Endpoints;

/// <summary>Retains the public validation message while identifying the invalid CLR argument.</summary>
internal sealed class RequestArgumentException : ArgumentException
{
    private readonly string _message;

    internal RequestArgumentException(string message, string paramName) : base(message, paramName) => _message = message;

    public override string Message => _message;
}
