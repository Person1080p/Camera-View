namespace CameraViewer.Api.Services;

/// <summary>An error whose message is safe to show to the user, with the HTTP status to report it as.</summary>
public class AppException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
