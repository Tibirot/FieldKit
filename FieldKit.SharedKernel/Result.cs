namespace FieldKit.SharedKernel;

/// <summary>A machine-readable error: a stable code plus a human message.</summary>
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
}

/// <summary>
/// Explicit success/failure without exceptions for expected errors.
/// Use for domain outcomes a caller is expected to handle.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    protected Result(bool isSuccess, Error error)
    {
        switch (isSuccess)
        {
            case true when error != Error.None:
                throw new InvalidOperationException("A successful result cannot carry an error.");
            case false when error == Error.None:
                throw new InvalidOperationException("A failed result must carry an error.");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);

    public static Result<T> Success<T>(T value) => new(value, true, Error.None);
    public static Result<T> Failure<T>(Error error) => new(default!, false, error);
}

/// <summary>A <see cref="Result"/> that carries a value on success.</summary>
public sealed class Result<T> : Result
{
    private readonly T _value;

    internal Result(T value, bool isSuccess, Error error) : base(isSuccess, error) => _value = value;

    /// <summary>The value. Throws if the result is a failure.</summary>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException("Cannot access the value of a failed result.");
}
