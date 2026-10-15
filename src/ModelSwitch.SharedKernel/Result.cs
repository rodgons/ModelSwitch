using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.SharedKernel;

/// <summary>Outcome of an operation that can fail in an expected way.</summary>
public readonly record struct Result
{
    private readonly Error? _error;

    private Result(Error? error) => _error = error;

    public bool IsSuccess => _error is null;

    public bool IsFailure => !IsSuccess;

    public Error Error => _error ?? throw new InvalidOperationException("A successful Result has no error.");

    public static Result Success() => default;

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);

    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>Outcome of an operation that returns <typeparamref name="T"/> or fails in an expected way.</summary>
public readonly record struct Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T? value, Error? error)
    {
        _value = value;
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("A failed Result has no value.");

    public Error Error => _error ?? throw new InvalidOperationException("A successful Result has no error.");

    internal static Result<T> Success(T value) => new(value, null);

    internal static Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map)
        => IsSuccess ? Result<TOut>.Success(map(_value!)) : Result<TOut>.Failure(_error!);
}
