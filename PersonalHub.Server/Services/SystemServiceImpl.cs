
using Grpc.Core;
using Microsoft.Extensions.Logging;
using personalHub;
using Personalhub;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using static Personalhub.System;

namespace personalHub.Server.Services
{
    public class SystemServiceImpl: SystemBase
    {
        //实现日志功能
        private readonly ILogger<SystemServiceImpl> _logger;
        public SystemServiceImpl(ILogger<SystemServiceImpl> logger)
        {
            _logger = logger;
        }

        public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
        {

            //Task.FromResult就是去匹配返回Task<T>
            return Task.FromResult(

                new PingResponse()
                {
                    Ok = true,
                    ServerVersion = "PersonalHub Server v1.0.0"

                }

                );
        }


    }
}
