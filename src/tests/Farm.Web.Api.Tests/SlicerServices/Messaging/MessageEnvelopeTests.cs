using Farm.Infrastructure;
using Farm.Infrastructure.Slicer.Messaging;

namespace Farm.Web.Api.Tests.SlicerServices.Messaging;

/// <summary>
/// Unit tests for MessageEnvelope class and idempotency functionality
/// </summary>
public class MessageEnvelopeTests
{
    [Fact]
    public void MessageEnvelope_Create_ShouldGenerateValidEnvelope()
    {
        // Arrange
        SlicingJobContent jobContent = new SlicingJobContent
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/model.stl",
            ModelFileName = "test-model.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = SlicingJobPriority.High
        };

        // Act
        MessageEnvelope envelope = MessageEnvelope.Create(jobContent, SlicerEngineType.OrcaSlicer, SlicingJobPriority.High);

        // Assert
        _ = envelope.Should().NotBeNull();
        _ = envelope.JobId.Should().NotBeEmpty();
        _ = envelope.SlicerType.Should().Be(SlicerEngineType.OrcaSlicer);
        _ = envelope.Priority.Should().Be(SlicingJobPriority.High);
        _ = envelope.Attempt.Should().Be(1);
        _ = envelope.CorrelationId.Should().NotBeEmpty();
        _ = envelope.Checksum.Should().NotBeNullOrEmpty();
        _ = envelope.SubmittedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        _ = envelope.Version.Should().Be(MessageEnvelope.CurrentVersion);
    }

    [Fact]
    public void MessageEnvelope_GenerateChecksum_ShouldBeConsistent()
    {
        // Arrange
        SlicingJobContent jobContent = new SlicingJobContent
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/model.stl",
            ModelFileName = "test-model.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer
        };

        // Act
        string checksum1 = MessageEnvelope.GenerateChecksum(jobContent);
        string checksum2 = MessageEnvelope.GenerateChecksum(jobContent);

        // Assert
        _ = checksum1.Should().NotBeNullOrEmpty();
        _ = checksum2.Should().NotBeNullOrEmpty();
        _ = checksum1.Should().Be(checksum2); // Same content should produce same checksum
    }

    [Fact]
    public void MessageEnvelope_GenerateChecksum_ShouldBeDifferentForDifferentContent()
    {
        // Arrange
        SlicingJobContent jobContent1 = new SlicingJobContent
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/model1.stl",
        };

        SlicingJobContent jobContent2 = new SlicingJobContent
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/model2.stl",
        };

        // Act
        string checksum1 = MessageEnvelope.GenerateChecksum(jobContent1);
        string checksum2 = MessageEnvelope.GenerateChecksum(jobContent2);

        // Assert
        _ = checksum1.Should().NotBe(checksum2);
    }

    [Fact]
    public void MessageEnvelope_ValidateChecksum_ShouldReturnTrueForMatchingContent()
    {
        // Arrange
        SlicingJobContent jobContent = new SlicingJobContent
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/model.stl",
        };

        MessageEnvelope envelope = MessageEnvelope.Create(jobContent, SlicerEngineType.OrcaSlicer);

        // Act
        bool isValid = envelope.ValidateChecksum(jobContent);

        // Assert
        _ = isValid.Should().BeTrue();
    }

    [Fact]
    public void MessageEnvelope_ValidateChecksum_ShouldReturnFalseForDifferentContent()
    {
        // Arrange
        SlicingJobContent originalContent = new SlicingJobContent
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/model.stl",
        };

        SlicingJobContent modifiedContent = new SlicingJobContent
        {
            UserId = originalContent.UserId,
            ModelFileUrl = "https://storage.example.com/different-model.stl", // Changed
        };

        MessageEnvelope envelope = MessageEnvelope.Create(originalContent, SlicerEngineType.OrcaSlicer);

        // Act
        bool isValid = envelope.ValidateChecksum(modifiedContent);

        // Assert
        _ = isValid.Should().BeFalse();
    }

    [Fact]
    public void MessageEnvelope_IsDuplicateOf_ShouldReturnTrueForSameCorrelationAndChecksum()
    {
        // Arrange
        Guid correlationId = Guid.NewGuid();
        SlicingJobContent jobContent = new SlicingJobContent
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/model.stl",
        };

        MessageEnvelope envelope1 = MessageEnvelope.Create(jobContent, SlicerEngineType.OrcaSlicer, correlationId: correlationId);
        MessageEnvelope envelope2 = MessageEnvelope.Create(jobContent, SlicerEngineType.OrcaSlicer, correlationId: correlationId);

        // Act
        bool isDuplicate = envelope1.IsDuplicateOf(envelope2);

        // Assert
        _ = isDuplicate.Should().BeTrue();
    }

    [Fact]
    public void MessageEnvelope_IsDuplicateOf_ShouldReturnFalseForDifferentCorrelation()
    {
        // Arrange
        SlicingJobContent jobContent = new SlicingJobContent { UserId = Guid.NewGuid(), ModelFileUrl = "https://storage.example.com/model.stl" };

        MessageEnvelope envelope1 = MessageEnvelope.Create(jobContent, SlicerEngineType.OrcaSlicer);
        MessageEnvelope envelope2 = MessageEnvelope.Create(jobContent, SlicerEngineType.OrcaSlicer);

        // Act
        bool isDuplicate = envelope1.IsDuplicateOf(envelope2);

        // Assert
        _ = isDuplicate.Should().BeFalse(); // Different correlation IDs
    }

    [Fact]
    public void MessageEnvelope_IsDuplicateOf_ShouldReturnFalseForDifferentChecksum()
    {
        // Arrange
        Guid correlationId = Guid.NewGuid();
        SlicingJobContent jobContent1 = new SlicingJobContent { UserId = Guid.NewGuid(), ModelFileUrl = "https://storage.example.com/model1.stl" };
        SlicingJobContent jobContent2 = new SlicingJobContent { UserId = Guid.NewGuid(), ModelFileUrl = "https://storage.example.com/model2.stl" };

        MessageEnvelope envelope1 = MessageEnvelope.Create(jobContent1, SlicerEngineType.OrcaSlicer, correlationId: correlationId);
        MessageEnvelope envelope2 = MessageEnvelope.Create(jobContent2, SlicerEngineType.OrcaSlicer, correlationId: correlationId);

        // Act
        bool isDuplicate = envelope1.IsDuplicateOf(envelope2);

        // Assert
        _ = isDuplicate.Should().BeFalse(); // Different checksums
    }

    [Fact]
    public void MessageEnvelope_CreateRetry_ShouldIncrementAttempt()
    {
        // Arrange
        SlicingJobContent jobContent = new SlicingJobContent { UserId = Guid.NewGuid(), ModelFileUrl = "https://storage.example.com/model.stl" };
        MessageEnvelope originalEnvelope = MessageEnvelope.Create(jobContent, SlicerEngineType.OrcaSlicer);

        // Act
        MessageEnvelope retryEnvelope = MessageEnvelope.CreateRetry(originalEnvelope);

        // Assert
        _ = retryEnvelope.Attempt.Should().Be(originalEnvelope.Attempt + 1);
        _ = retryEnvelope.CorrelationId.Should().Be(originalEnvelope.CorrelationId);
        _ = retryEnvelope.Checksum.Should().Be(originalEnvelope.Checksum);
        _ = retryEnvelope.JobId.Should().NotBe(originalEnvelope.JobId); // New job ID for retry
        _ = retryEnvelope.SubmittedAt.Should().BeAfter(originalEnvelope.SubmittedAt);
    }

    [Fact]
    public void SlicingJobContent_FromRequest_ShouldCreateCorrectContent()
    {
        // Arrange
        SlicingJobRequest request = new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = new Uri("https://storage.example.com/model.stl"),
            ModelFileName = "test-model.stl",
            SlicerEngine = SlicerEngineType.PrusaSlicer,
            Priority = SlicingJobPriority.High,
            SlicerProfile = new SlicerProfileDto
            {
                ProcessProfile = new ProcessProfileDto { LayerHeight = 0.2, Name = "Test", Quality = "standard" }
            }
        };
        request.Metadata["test"] = "value";

        // Act
        SlicingJobContent content = SlicingJobContent.FromRequest(request);

        // Assert
        _ = content.UserId.Should().Be(request.UserId);
        _ = content.PrinterId.Should().Be(request.PrinterId);
        _ = content.ModelFileUrl.Should().Be(request.ModelFileUrl.ToString());
        _ = content.ModelFileName.Should().Be(request.ModelFileName);
        _ = content.SlicerEngine.Should().Be(request.SlicerEngine);
        _ = content.Priority.Should().Be(request.Priority);
        _ = content.SlicerProfile.Should().Be(request.SlicerProfile);
        _ = content.Metadata.Should().Equal(request.Metadata);
    }

    [Fact]
    public void SlicingJobRequest_GetOrCreateEnvelope_ShouldCreateEnvelopeIfNone()
    {
        // Arrange
        SlicingJobRequest request = new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = new Uri("https://storage.example.com/model.stl"),
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = SlicingJobPriority.Normal
        };

        // Act
        MessageEnvelope envelope = request.GetOrCreateEnvelope();

        // Assert
        _ = envelope.Should().NotBeNull();
        _ = envelope.SlicerType.Should().Be(SlicerEngineType.OrcaSlicer);
        _ = envelope.Priority.Should().Be(SlicingJobPriority.Normal);
    }

    [Fact]
    public void SlicingJobRequest_GetOrCreateEnvelope_ShouldReturnExistingEnvelope()
    {
        // Arrange
        MessageEnvelope existingEnvelope = new MessageEnvelope
        {
            SlicerType = SlicerEngineType.PrusaSlicer,
            Priority = SlicingJobPriority.High,
            CorrelationId = Guid.NewGuid()
        };

        SlicingJobRequest request = new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = new Uri("https://storage.example.com/model.stl"),
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Envelope = existingEnvelope
        };

        // Act
        MessageEnvelope envelope = request.GetOrCreateEnvelope();

        // Assert
        _ = envelope.Should().BeSameAs(existingEnvelope);
    }
}
