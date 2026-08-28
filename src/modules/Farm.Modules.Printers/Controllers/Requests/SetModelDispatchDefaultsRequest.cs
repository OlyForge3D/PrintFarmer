using Farm.Infrastructure.Domain;

namespace Farm.Modules.Printers.Controllers.Requests;

/// <summary>
/// Request to set dispatch defaults on a printer model.
/// </summary>
public record SetModelDispatchDefaultsRequest(
    AutoDispatchState DefaultAutoDispatchState = AutoDispatchState.None,
    StartBehavior? DefaultStartBehavior = null);
