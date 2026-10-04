using Microsoft.AspNetCore.Mvc;

using PPather;
using PPather.Graph;

namespace Frontend.Controllers;

[Route("api/[controller]")]
[ApiController]
public class DangerZoneController : ControllerBase
{
    private readonly DataConfig dataConfig;
    private readonly PPatherService service;

    public DangerZoneController(DataConfig dataConfig, PPatherService service)
    {
        this.dataConfig = dataConfig;
        this.service = service;
    }

    /// <summary>
    /// Re-reads the authored roads and danger zones. These are query-time costs,
    /// so this takes effect on the next path with no rebake.
    /// </summary>
    [HttpPost("reload")]
    public IActionResult Reload()
    {
        service.ReloadCostZones();
        return Ok();
    }

    [HttpGet]
    public IActionResult Get([FromQuery] string continent)
    {
        if (string.IsNullOrWhiteSpace(continent))
            return BadRequest(UIMapJsonFiles.ContinentRequired);

        return Ok(UIMapJsonFiles.ReadAll<DangerZoneData>(DangerDir(continent)));
    }

    [HttpPost]
    public IActionResult Post([FromQuery] string continent, [FromQuery] int uiMapId, [FromBody] DangerZoneData data)
    {
        if (string.IsNullOrWhiteSpace(continent))
            return BadRequest(UIMapJsonFiles.ContinentRequired);

        UIMapJsonFiles.Write(DangerDir(continent), uiMapId, data,
            empty: data.Circles.Length == 0 && data.Rectangles.Length == 0);

        return Ok();
    }

    private string DangerDir(string continent)
    {
        return System.IO.Path.Join(dataConfig.Road, continent, "dangerzone");
    }
}
