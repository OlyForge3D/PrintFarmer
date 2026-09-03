namespace Farm.Slicer.Module.Services;

/// <summary>Opens verified promotion-source bytes without exposing their storage location.</summary>
public interface IPromotionArtifactContentSource
{
    /// <summary>Opens one artifact only for the operation that currently pins it.</summary>
    /// <param name="artifactId">Artifact identifier.</param>
    /// <param name="operationKey">Owner-scoped operation identity holding the active pin.</param>
    /// <param name="expectedSizeBytes">Authoritative byte count from the artifact metadata row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The owned content stream, or <see langword="null"/> when the bytes do not exist.</returns>
    Task<PromotionArtifactContent?> OpenReadAsync(
        Guid artifactId,
        string operationKey,
        long expectedSizeBytes,
        CancellationToken cancellationToken);
}

/// <summary>An owned, exact-length stream over promotion-source bytes.</summary>
public sealed class PromotionArtifactContent : IAsyncDisposable
{
    private readonly Func<ValueTask> _disposeAsync;

    private PromotionArtifactContent(
        Stream content,
        long expectedSizeBytes,
        Func<ValueTask> disposeAsync,
        CancellationToken transportCancellationToken)
    {
        Content = new ExactLengthReadStream(
            content,
            expectedSizeBytes,
            transportCancellationToken);
        _disposeAsync = disposeAsync;
    }

    /// <summary>Gets the exact-length read stream.</summary>
    public Stream Content { get; }

    /// <summary>Creates a content lease whose owner controls the underlying stream lifetime.</summary>
    /// <param name="content">Underlying source stream.</param>
    /// <param name="expectedSizeBytes">Required byte count.</param>
    /// <param name="disposeAsync">Callback that releases the underlying source lease.</param>
    /// <param name="transportCancellationToken">Optional lifetime token for transport timeouts.</param>
    /// <returns>The owned promotion content.</returns>
    public static PromotionArtifactContent Create(
        Stream content,
        long expectedSizeBytes,
        Func<ValueTask> disposeAsync,
        CancellationToken transportCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(disposeAsync);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSizeBytes);
        return new PromotionArtifactContent(
            content,
            expectedSizeBytes,
            disposeAsync,
            transportCancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        await _disposeAsync().ConfigureAwait(false);
    }

    private sealed class ExactLengthReadStream(
        Stream inner,
        long expectedLength,
        CancellationToken transportCancellationToken) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => expectedLength;

        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return RecordRead(inner.Read(buffer, offset, count));
            }
            catch (IOException exception)
            {
                throw new PromotionSourceTransportException("Promotion source stream failed.", exception);
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    transportCancellationToken);
                int read = await inner.ReadAsync(buffer, linkedSource.Token).ConfigureAwait(false);
                return RecordRead(read);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested &&
                      transportCancellationToken.IsCancellationRequested)
            {
                throw new PromotionSourceTransportException("Promotion source stream timed out.", exception);
            }
            catch (IOException exception)
            {
                throw new PromotionSourceTransportException("Promotion source stream failed.", exception);
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int RecordRead(int count)
        {
            if (count == 0 && _bytesRead < expectedLength)
            {
                throw new PromotionSourceTransportException("Promotion source stream ended before its declared length.");
            }

            _bytesRead += count;
            if (_bytesRead > expectedLength)
            {
                throw new PromotionSourceTransportException("Promotion source stream exceeded its declared length.");
            }

            return count;
        }
    }
}

/// <summary>Retryable failure while acquiring or reading promotion-source bytes.</summary>
public sealed class PromotionSourceTransportException : IOException
{
    /// <summary>Creates a retryable source transport failure.</summary>
    public PromotionSourceTransportException()
    {
    }

    /// <summary>Creates a retryable source transport failure.</summary>
    /// <param name="message">Non-sensitive failure description.</param>
    public PromotionSourceTransportException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a retryable source transport failure with its underlying cause.</summary>
    /// <param name="message">Non-sensitive failure description.</param>
    /// <param name="innerException">Underlying transport exception.</param>
    public PromotionSourceTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Indicates that the source pin was completed or replaced while another request was resolving
/// the same promotion operation.
/// </summary>
public sealed class PromotionSourcePinMismatchException : IOException
{
    /// <summary>Creates a pin-race signal that callers must resolve through durable promotion state.</summary>
    public PromotionSourcePinMismatchException()
        : base("The promotion source pin no longer matches the requested operation.")
    {
    }

    /// <summary>Creates a pin-race signal with a non-sensitive description.</summary>
    /// <param name="message">Non-sensitive failure description.</param>
    public PromotionSourcePinMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a pin-race signal with its underlying cause.</summary>
    /// <param name="message">Non-sensitive failure description.</param>
    /// <param name="innerException">Underlying failure.</param>
    public PromotionSourcePinMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
