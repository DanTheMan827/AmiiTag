namespace AmiiTag.Tags;

/// <summary>
/// An error with a message that is suitable to show to the user.
/// </summary>
public class TagException : Exception
{
    public TagException(string message) : base(message)
    {
    }

    public TagException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
