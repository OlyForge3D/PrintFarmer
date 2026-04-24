using Farm.Infrastructure.Services.MaterialClusters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages material equivalence clusters for grouping equivalent filament types
/// from different vendors, enabling fuzzy matching during auto-dispatch.
/// </summary>
[ApiController]
[Route("api/material-clusters")]
[Tags("Material Clusters")]
[Authorize]
public class MaterialClusterController(IMaterialClusterService clusterService) : ControllerBase
{
    /// <summary>Gets all material clusters with their members.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<MaterialClusterDto>), 200)]
    public async Task<IActionResult> GetAllAsync(CancellationToken ct)
    {
        List<MaterialClusterDto> clusters = await clusterService.GetAllClustersAsync(ct);
        return Ok(clusters);
    }

    /// <summary>Gets a single material cluster by ID.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(MaterialClusterDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetByIdAsync(Guid id, CancellationToken ct)
    {
        MaterialClusterDto? cluster = await clusterService.GetClusterByIdAsync(id, ct);
        return cluster is null ? NotFound() : Ok(cluster);
    }

    /// <summary>Creates a new material cluster, optionally with initial filament type members.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(MaterialClusterDto), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateAsync([FromBody] CreateMaterialClusterRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Cluster name is required." });
        }

        MaterialClusterDto cluster = await clusterService.CreateClusterAsync(request, ct);
        return CreatedAtAction(nameof(GetByIdAsync), new { id = cluster.Id }, cluster);
    }

    /// <summary>Updates an existing material cluster's name and description.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(MaterialClusterDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] UpdateMaterialClusterRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Cluster name is required." });
        }

        MaterialClusterDto? updated = await clusterService.UpdateClusterAsync(id, request, ct);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Deletes a material cluster and all its memberships.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        bool deleted = await clusterService.DeleteClusterAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>Adds a filament type to a cluster.</summary>
    [HttpPost("{clusterId:guid}/members/{filamentTypeId:guid}")]
    [ProducesResponseType(typeof(MaterialClusterDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> AddMemberAsync(Guid clusterId, Guid filamentTypeId, CancellationToken ct)
    {
        MaterialClusterDto? result = await clusterService.AddMemberAsync(clusterId, filamentTypeId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Removes a filament type from a cluster.</summary>
    [HttpDelete("{clusterId:guid}/members/{filamentTypeId:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RemoveMemberAsync(Guid clusterId, Guid filamentTypeId, CancellationToken ct)
    {
        bool removed = await clusterService.RemoveMemberAsync(clusterId, filamentTypeId, ct);
        return removed ? NoContent() : NotFound();
    }
}
