//
// Azure Media Services REST API v3 Functions
//
// GetAssetUrls - This function provides URLs for the asset.
//
/*
```c#
Input:
    {
        // [Required] The name of the streaming locator.
        "streamingLocatorName": "streaminglocator-911b65de-ac92-4391-9aab-80021126d403",

        // The name of the streaming endpoint; "default" is used by default
        "streamingEndpointName": "default",

        // The scheme of the streaming URL; "http" or "https", and "https" is used by default
        "streamingUrlScheme": "https"
    }
Output:
    {
        // The path list of Progressive Download
        "downloadPaths": [],
        // The path list of Streaming
        "streamingPaths": [
            {
                // Streaming Protocol
                "StreamingProtocol": "Hls",
                // Encryption Scheme
                "EncryptionScheme": "EnvelopeEncryption",
                // Streaming URL
                "StreamingUrl": "https://amsv3demo-jpea.streaming.media.azure.net/6c4bb037-6907-406d-8e4d-15f91e44ac08/Ignite-short.ism/manifest(format=m3u8-aapl,encryption=cbc)"
            },
            {
                "StreamingProtocol": "Dash",
                "EncryptionScheme": "EnvelopeEncryption",
                "StreamingUrl": "https://amsv3demo-jpea.streaming.media.azure.net/6c4bb037-6907-406d-8e4d-15f91e44ac08/Ignite-short.ism/manifest(format=mpd-time-csf,encryption=cbc)"
            },
            {
                "StreamingProtocol": "SmoothStreaming",
                "EncryptionScheme": "EnvelopeEncryption",
                "StreamingUrl": "https://amsv3demo-jpea.streaming.media.azure.net/6c4bb037-6907-406d-8e4d-15f91e44ac08/Ignite-short.ism/manifest(encryption=cbc)"
            }
        ]
    }

```
*/
//
//

using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using advanced_vod_functions_v3.SharedLibs;
using System.Collections.Generic;

namespace advanced_vod_functions_v3
{

    public class UploadResponse {
        public List<UploadResponseStrem> streamingPaths { get; set; }
        public string fileName { get; set; }

    }

    public class UploadResponseStrem
    {
        public string StreamingProtocol { get; set; }
        public string EncryptionScheme { get; set; }
        public string StreamingUrl { get; set; }
    }

    public static class UpdateAssetDB
    {
        [FunctionName("UpdateAssetDB")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation($"AMS v3 Function - Update Asset in DB was triggered!");

            try
            {

                string requestBody = new StreamReader(req.Body).ReadToEnd();
                UploadResponse data = JsonConvert.DeserializeObject<UploadResponse>(requestBody);

                string fileName = data.fileName;
                string streamUrl = "";

                foreach (var stream in data.streamingPaths) {
                    if (stream.StreamingProtocol == "Hls") {
                        streamUrl = stream.StreamingUrl;
                    }
                }


                JObject result = new JObject();
                result["fileName"] = fileName;
                result["streamUrl"] = streamUrl;
                 return (ActionResult)new OkObjectResult(result);
            }
            catch (Exception e)
            {
                log.LogInformation($"ERROR: Exception with message: {e.Message}");
                return new BadRequestObjectResult("Error: " + e.Message);
            }


        }
    }
}
