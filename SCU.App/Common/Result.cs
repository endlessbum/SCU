namespace SCU.Common;

public sealed class Result
{
    private Result(bool isSuccess, string message, int code)
    {
        IsSuccess = isSuccess;
        Message = message;
        Code = code;
    }

    public bool IsSuccess { get; }
    public string Message { get; }
    public int Code { get; }

    public static Result Success(string message = "") => new(true, message, 0);

    // Успех с оговоркой (код > 0): операция выполнена, но не полностью —
    // например Restore пропустил значения из бэкапа старого формата. Вызывающий
    // код по такому коду решает, можно ли уничтожать источник восстановления.
    public static Result Success(string message, int code) => new(true, message, code);

    public static Result Failure(string message, int code = 1) => new(false, message, code);
}

public sealed class Result<T>
{
    private Result(bool isSuccess, T? value, string message, int code)
    {
        IsSuccess = isSuccess;
        Value = value;
        Message = message;
        Code = code;
    }

    public bool IsSuccess { get; }
    public T? Value { get; }
    public string Message { get; }
    public int Code { get; }

    public static Result<T> Success(T value, string message = "") => new(true, value, message, 0);

    public static Result<T> Failure(string message, int code = 1) => new(false, default, message, code);
}
