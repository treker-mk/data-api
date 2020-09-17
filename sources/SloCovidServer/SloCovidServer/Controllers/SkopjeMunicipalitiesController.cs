﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SloCovidServer.Models;
using SloCovidServer.Services.Abstract;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace SloCovidServer.Controllers
{
    [ApiController]
    [Route("api/skopje-municipalities")]
    public class SkopjeMunicipalitiesController : MetricsController<SkopjeMunicipalitiesController>
    {
        public SkopjeMunicipalitiesController(ILogger<SkopjeMunicipalitiesController> logger, ICommunicator communicator) : base(logger, communicator)
        {
        }
        [HttpGet]
        public async Task<ActionResult<ImmutableArray<MunicipalityDay>?>> Get()
        {
            return await ProcessRequestAsync(communicator.GetSkopjeMunicipalitiesAsync);
        }
    }
}
