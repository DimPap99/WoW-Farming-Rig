using Microsoft.AspNetCore.Mvc;

using PPather;
using PPather.Graph;

namespace Frontend.Controllers;

[Route("api/[controller]")]
[ApiController]
public class RoadController : ControllerBase
{
    private readonly DataConfig dataConfig;
    private readonly PPatherService service;

    public RoadController(DataConfig dataConfig, PPatherService service)
    {
        this.dataConfig = dataConfig;
        this.service = service;
    }

    [HttpGet]
    public IActionResult Get([FromQuery] string continent)
    {
        if (string.IsNullOrWhiteSpace(continent))
            return BadRequest(UIMapJsonFiles.ContinentRequired);

        string continentDir = System.IO.Path.Join(dataConfig.Road, continent);
        if (Directory.Exists(continentDir))
        {
            return Ok(UIMapJsonFiles.ReadAll<RoadData>(continentDir));
        }

        // Fallback: legacy single-file format
        string legacyPath = System.IO.Path.Join(dataConfig.Road, continent + ".json");
        if (System.IO.File.Exists(legacyPath))
        {
            Dictionary<int, RoadData> result = new() { [0] = UIMapJsonFiles.Read<RoadData>(legacyPath) };
            return Ok(result);
        }

        return Ok(new Dictionary<int, RoadData>());
    }

    [HttpPost]
    public IActionResult Post([FromQuery] string continent, [FromQuery] int uiMapId, [FromBody] RoadData data)
    {
        if (string.IsNullOrWhiteSpace(continent))
            return BadRequest(UIMapJsonFiles.ContinentRequired);

        UIMapJsonFiles.Write(System.IO.Path.Join(dataConfig.Road, continent), uiMapId, data,
            empty: data.Roads.Length == 0);

        return Ok();
    }

    [HttpPost("reload")]
    public IActionResult Reload()
    {
        service.ReloadCostZones();
        return Ok();
    }
}
