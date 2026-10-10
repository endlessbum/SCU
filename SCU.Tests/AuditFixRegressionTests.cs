using System.IO.Pipes;
using System.Text;
using SCU.AppCore.AI;
using SCU.Interop;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// Регрессии аудита 2026-10.

// Аудит: retry в ConsoleOutputDecoder был мёртвым кодом — счётчик
// инкрементировался без повторного чтения, первый же IOException обрывал поток
// и терял остаток вывода ScannerCore.
public sealed class ConsoleOutputDecoderRetryTests
{
    private sealed class FlakyStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        private int _failuresLeft;

        public FlakyStream(Queue<byte[]> chunks, int failuresLeft)
        {
            _chunks = chunks;
            _failuresLeft = failuresLeft;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new IOException("transient pipe error");
            }

            if (_chunks.Count == 0)
            {
                return 0;
            }

            var chunk = _chunks.Dequeue();
            Array.Copy(chunk, buffer, chunk.Length);
            return chunk.Length;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                await Task.Delay(10, cancellationToken);
                throw new IOException("transient pipe error");
            }

            if (_chunks.Count == 0)
            {
                return 0;
            }

            var chunk = _chunks.Dequeue();
            chunk.CopyTo(buffer);
            return chunk.Length;
        }
    }

    [Fact]
    public async Task TransientIOException_IsRetriedAndOutputIsNotLost()
    {
        // IOException, затем данные, затем конец: раньше весь хвост терялся.
        var stream = new FlakyStream(
            new Queue<byte[]>([Encoding.UTF8.GetBytes("line-after-error\n")]),
            failuresLeft: 1);

        var lines = new List<string>();
        await ConsoleOutputDecoder.ReadLinesAsync(stream, lines.Add, CancellationToken.None);

        Assert.Contains("line-after-error", lines);
    }

    [Fact]
    public async Task PersistentIOException_EndsReadingGracefully()
    {
        // Все попытки истекли — чтение завершается без исключения.
        var stream = new FlakyStream(new Queue<byte[]>([Encoding.UTF8.GetBytes("never\n")]), failuresLeft: 99);

        var lines = new List<string>();
        await ConsoleOutputDecoder.ReadLinesAsync(stream, lines.Add, CancellationToken.None);

        Assert.Empty(lines);
    }
}

// Аудит AI: история агентного диалога росла без ограничения — обрезка по окну
// последних сообщений с границей на user-сообщении.
public sealed class AgentHistoryTrimTests
{
    private static List<ScuAiAgentMessage> BuildHistory(int turns)
    {
        var history = new List<ScuAiAgentMessage>();
        for (var i = 0; i < turns; i++)
        {
            history.Add(ScuAiAgentMessage.User($"question {i}"));
            history.Add(ScuAiAgentMessage.Assistant($"answer {i}", null));
        }

        return history;
    }

    [Fact]
    public void ShortHistory_Untouched()
    {
        var history = BuildHistory(5);

        ScuAiAssistant.TrimHistory(history);

        Assert.Equal(10, history.Count);
    }

    [Fact]
    public void LongHistory_TrimmedToWindowStartingAtUserMessage()
    {
        var history = BuildHistory(30); // 60 сообщений

        ScuAiAssistant.TrimHistory(history);

        Assert.True(history.Count <= ScuAiAssistant.MaxHistoryMessages);
        // Окно начинается с user-сообщения: порядок tool/assistant валиден.
        Assert.Equal("user", history[0].Role);
        Assert.Equal("question 10", history[0].Content);
    }
}
