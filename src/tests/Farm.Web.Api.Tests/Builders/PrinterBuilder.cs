using System;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Tests.Builders;

/// <summary>
/// Builder for creating Printer test objects with fluent API
/// </summary>
public class PrinterBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _name = "Test Printer";
    private string _serverUrl = "http://192.168.1.100";
    private int _backendPort = 7125;
    private int _backend = (int)PrinterBackend.Moonraker;
    private PrinterCapabilities? _capabilities;
    private PrinterModel? _model;
    private string? _apiKey;
    private string? _notes;
    private Guid _manufacturerId = Guid.NewGuid();
    private Guid _modelId = Guid.NewGuid();

    public PrinterBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public PrinterBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public PrinterBuilder WithServerUrl(string serverUrl)
    {
        _serverUrl = serverUrl;
        return this;
    }

    public PrinterBuilder WithBackendPort(int port)
    {
        _backendPort = port;
        return this;
    }

    public PrinterBuilder WithBackend(PrinterBackend backend)
    {
        _backend = (int)backend;
        return this;
    }

    public PrinterBuilder WithCapabilities(PrinterCapabilities capabilities)
    {
        _capabilities = capabilities;
        return this;
    }

    public PrinterBuilder WithModel(PrinterModel model)
    {
        _model = model;
        _modelId = model.Id;
        return this;
    }

    public PrinterBuilder WithApiKey(string apiKey)
    {
        _apiKey = apiKey;
        return this;
    }

    public PrinterBuilder WithNotes(string notes)
    {
        _notes = notes;
        return this;
    }

    /// <summary>
    /// Creates an online printer ready to accept jobs
    /// </summary>
    public PrinterBuilder AsOnlineAndReady()
    {
        _capabilities = new PrinterCapabilities
        {
            IsAvailable = true
        };
        return this;
    }

    /// <summary>
    /// Creates an offline printer
    /// </summary>
    public PrinterBuilder AsOffline()
    {
        if (_capabilities != null)
        {
            _capabilities.IsAvailable = false;
        }
        else
        {
            _capabilities = new PrinterCapabilities { IsAvailable = false };
        }
        return this;
    }

    /// <summary>
    /// Creates a printing printer
    /// </summary>
    public PrinterBuilder AsPrinting()
    {
        if (_capabilities != null)
        {
            _capabilities.IsAvailable = false;
        }
        else
        {
            _capabilities = new PrinterCapabilities { IsAvailable = false };
        }
        return this;
    }

    /// <summary>
    /// Creates a Moonraker printer
    /// </summary>
    public PrinterBuilder AsMoonrakerPrinter()
    {
        _backend = (int)PrinterBackend.Moonraker;
        _backendPort = 7125;
        return this;
    }

    /// <summary>
    /// Creates a PrusaLink printer
    /// </summary>
    public PrinterBuilder AsPrusaLinkPrinter()
    {
        _backend = (int)PrinterBackend.PrusaLink;
        _backendPort = 80;
        return this;
    }

    public Printer Build()
    {
        return new Printer
        {
            Id = _id,
            Name = _name,
            ServerUrl = _serverUrl,
            BackendPort = _backendPort,
            Backend = _backend,
            Capabilities = _capabilities,
            Model = _model,
            ApiKey = _apiKey,
            Notes = _notes,
            ManufacturerId = _manufacturerId,
            ModelId = _modelId
        };
    }
}
