using System.Text.Json;
using System.Text.Json.Nodes;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

public partial class SliceJobController
{
    private async Task<IActionResult?> BindExplicitProfilesAsync(
        SliceJob job,
        SubmitSliceJobRequest request,
        Guid userId,
        CancellationToken ct)
    {
        SliceProfileSelections selections = request.ProfileSelections!;
        if (request.SlicerEngine != SlicerEngineType.OrcaSlicer ||
            request.MachineProfileId.HasValue || request.ProcessProfileId.HasValue ||
            request.FilamentProfileId.HasValue ||
            selections.Machine is null || selections.Process is null ||
            selections.Filaments is null || selections.Filaments.Count == 0 ||
            !ValidSelection(selections.Machine) || !ValidSelection(selections.Process) ||
            selections.Filaments.Any(selection => !ValidSelection(selection)) ||
            (request.SlicerProfileId.HasValue && request.SlicerProfileId != selections.Process.CustomProfileId))
        {
            return InvalidProfileSelection();
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(job.SlicerProfileJson ?? "{}");
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return InvalidProfileSelection();
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !SelectionNameMatches(root, "machineProfileName", selections.Machine.Name) ||
            !SelectionNameMatches(root, "processProfileName", selections.Process.Name))
        {
            return InvalidProfileSelection();
        }

        string[] filamentNames = selections.Filaments.Select(selection => selection.Name).ToArray();
        string[] expectedNames = request.ExtruderFilamentProfileNames is { Count: > 0 }
            ? request.ExtruderFilamentProfileNames.ToArray()
            : [GetJsonString(root, "filamentProfileName") ?? string.Empty];
        if (!filamentNames.SequenceEqual(expectedNames, StringComparer.Ordinal) ||
            (root.TryGetProperty("filamentProfileNames", out JsonElement embeddedNames) &&
             (embeddedNames.ValueKind != JsonValueKind.Array ||
              !embeddedNames.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                  .SequenceEqual(filamentNames, StringComparer.Ordinal))))
        {
            return InvalidProfileSelection();
        }

        if (_machineProfileRepository is null || _processProfileRepository is null ||
            _filamentProfileRepository is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        AllProfilesResponseDto? stock = null;
        if (selections.Machine.Source == "system" || selections.Process.Source == "system" ||
            selections.Filaments.Any(selection => selection.Source == "system"))
        {
            if (_profilesService is null || _httpClientFactory is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                using HttpClient client = _httpClientFactory.CreateClient();
                stock = await _profilesService.GetWorkerProfilesHierarchyAsync(client, ct, job.SlicerEngineVersion);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Explicit profile library unavailable for job {JobId}", job.Id);
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (stock is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        bool CanRead(bool system, bool publicProfile, Guid? owner, SlicerType engine, string name, SliceProfileSelection selection) =>
            !system && engine == SlicerType.OrcaSlicer &&
            string.Equals(name, selection.Name, StringComparison.Ordinal) &&
            (owner == userId || publicProfile || PrintFarmerPermissions.IsFarmAdmin(User));

        string? machineJson;
        if (selections.Machine.CustomProfileId is { } machineId)
        {
            MachineProfile? machine = await _machineProfileRepository.GetByIdAsync(machineId, ct);
            machineJson = machine is not null && !string.IsNullOrWhiteSpace(machine.RawJson) &&
                CanRead(machine.IsSystem, machine.IsPublic, machine.CreatedByUserId, machine.SlicerType, machine.Name, selections.Machine)
                ? ExtractFlatNativeProfileJson(machine.RawJson) : null;
        }
        else
        {
            machineJson = UniqueStockJson(
                stock!.MachineProfiles.Values.SelectMany(values => values),
                selections.Machine.Name, profile => profile.Name, profile => profile.Settings);
        }

        string? processJson;
        if (selections.Process.CustomProfileId is { } processId)
        {
            ProcessProfile? process = await _processProfileRepository.GetByIdAsync(processId, ct);
            processJson = process is not null && !string.IsNullOrWhiteSpace(process.RawJson) &&
                CanRead(process.IsSystem, process.IsPublic, process.CreatedByUserId, process.SlicerType, process.Name, selections.Process)
                ? ExtractFlatNativeProfileJson(process.RawJson) : null;
        }
        else
        {
            processJson = UniqueStockJson(
                stock!.ProcessProfiles.Values.SelectMany(values => values),
                selections.Process.Name, profile => profile.Name, profile => profile.Settings);
        }

        var filaments = new List<NativeFilamentProfile>();
        for (int index = 0; index < selections.Filaments.Count; index++)
        {
            SliceProfileSelection selection = selections.Filaments[index];
            string? json;
            if (selection.CustomProfileId is { } filamentId)
            {
                FilamentProfile? filament = await _filamentProfileRepository.GetByIdAsync(filamentId, ct);
                json = filament is not null && !string.IsNullOrWhiteSpace(filament.RawJson) &&
                    CanRead(filament.IsSystem, filament.IsPublic, filament.CreatedByUserId, filament.SlicerType, filament.Name, selection)
                    ? ExtractFlatNativeProfileJson(filament.RawJson) : null;
            }
            else
            {
                json = UniqueStockJson(
                    stock!.FilamentProfiles.Values.SelectMany(values => values),
                    selection.Name, profile => profile.Name, profile => profile.Settings);
            }

            if (json is null || !IsFlatNativeProfileJson(json))
            {
                return InvalidProfileSelection();
            }

            if (root.TryGetProperty("filamentColours", out JsonElement colours) &&
                colours.ValueKind == JsonValueKind.Array && index < colours.GetArrayLength() &&
                colours[index].ValueKind == JsonValueKind.String)
            {
                JsonObject native = JsonNode.Parse(json)!.AsObject();
                native["filament_colour"] = new JsonArray(JsonValue.Create(colours[index].GetString()));
                json = native.ToJsonString();
            }
            else if (selections.Filaments.Count == 1)
            {
                json = ApplyFilamentColourOverride(json, root);
            }

            filaments.Add(new NativeFilamentProfile(json, ComputeSha256(json)));
        }

        if (machineJson is null || processJson is null ||
            !IsFlatNativeProfileJson(machineJson) || !IsFlatNativeProfileJson(processJson))
        {
            return InvalidProfileSelection();
        }

        processJson = ApplyProcessOverrides(processJson, root);
        job.MachineProfileJson = machineJson;
        job.ProcessProfileJson = processJson;
        job.FilamentProfileJson = filaments[0].Json;
        job.MachineProfileSha256 = ComputeSha256(machineJson);
        job.ProcessProfileSha256 = ComputeSha256(processJson);
        job.FilamentProfileSha256 = ComputeSha256(string.Join('\0', filaments.Select(profile => profile.Json)));
        job.ExtruderFilamentProfilesJson = JsonSerializer.Serialize(filaments);

        // Claim matching is OR-any, so a separate generic tag would let an old worker claim this job.
        string capability = job.SlicerEngineVersion is { Length: > 0 } version
            ? $"orcaslicer:{version}:native-profile-selections-v1"
            : "orcaslicer:native-profile-selections-v1";
        job.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { capability });
        return null;
    }

    private ObjectResult InvalidProfileSelection() => SlicerApiProblems.InvalidRequest(
        this, "invalid_profile_selection",
        "A selected profile is missing, renamed, inaccessible, ambiguous or does not match its source. Select the profiles again.");

    private static bool ValidSelection(SliceProfileSelection? selection) =>
        selection is not null && !string.IsNullOrWhiteSpace(selection.Name) &&
        ((selection.Source == "system" && selection.CustomProfileId is null) ||
         (selection.Source == "user" && selection.CustomProfileId is { } id && id != Guid.Empty));

    private static bool SelectionNameMatches(JsonElement root, string property, string name) =>
        string.Equals(GetJsonString(root, property), name, StringComparison.Ordinal);

    private static string? UniqueStockJson<T>(
        IEnumerable<T> profiles, string name, Func<T, string> getName, Func<T, Dictionary<string, object>> getSettings)
    {
        List<T> matches = profiles.Where(profile =>
            string.Equals(getName(profile), name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? JsonSerializer.Serialize(getSettings(matches[0])) : null;
    }
}
