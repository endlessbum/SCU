using SCU.Common;
using Xunit;

namespace SCU.Tests;

// Форматирование карточки перехватчика системных уведомлений: корневое
// исключение, распаковка AggregateException, обрезка длинных сообщений.
public sealed class SystemNotificationInterceptorTests
{
    [Fact]
    public void Format_NestedInnerException_TakesRootCause()
    {
        var exception = new InvalidOperationException("внешний",
            new ArgumentException("внутренний"));

        var text = SystemNotificationInterceptor.Format(exception);

        Assert.Equal("ArgumentException: внутренний", text);
    }

    [Fact]
    public void Format_AggregateException_UnwrapsFirstInner()
    {
        var exception = new AggregateException(
            new InvalidOperationException("первый"),
            new InvalidOperationException("второй"));

        var text = SystemNotificationInterceptor.Format(exception);

        Assert.Equal("InvalidOperationException: первый", text);
    }

    // У дефолтного Exception подставляется системное сообщение, поэтому
    // пустой Message даёт только собственный класс-исключение.
    private sealed class EmptyMessageException : Exception
    {
        public override string Message => string.Empty;
    }

    [Fact]
    public void Format_EmptyMessage_UsesTypeName()
    {
        var text = SystemNotificationInterceptor.Format(new EmptyMessageException());

        Assert.Equal("EmptyMessageException", text);
    }

    [Fact]
    public void Format_VeryLongMessage_TruncatedWithEllipsis()
    {
        var exception = new Exception(new string('x', 500));

        var text = SystemNotificationInterceptor.Format(exception);

        Assert.Equal(301, text.Length);
        Assert.EndsWith("…", text);
    }
}
