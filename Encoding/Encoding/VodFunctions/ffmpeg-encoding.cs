//
// 
//
// ffmpeg -  This function encode with ffmpeg. Recommended is to deploy using the Premium plan
//
/*
```c#
Input :
{
    "inputFileName":"",
    "storageAccount":"",
    "storageKey":"",
    "outputAccount":"",
    "outputKey":"",
    "ffmpegArguments" : " -i {input} {output} -y"
}


```
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Encoding.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Encoding
{
    public static class fmpeg
    {

        public static async Task<List<IListBlobItem>> ListBlobsAsync(CloudBlobContainer container)
        {
            BlobContinuationToken continuationToken = null; //start at the beginning
            var results = new List<IListBlobItem>();
            do
            {
                var response = await container.ListBlobsSegmentedAsync(continuationToken);
                continuationToken = response.ContinuationToken;
                results.AddRange(response.Results);
            }

            while (continuationToken != null); //when this is null again, we've reached the end
            return results;
        }

        [FunctionName("ffmpeg-encoding")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]
            HttpRequest req, ILogger log, ExecutionContext context)
        {
            string output = string.Empty;
            bool isSuccessful = true;
            dynamic ffmpegResult = new JObject();
            string errorText = string.Empty;
            int exitCode = 0;

            log.LogInformation("C# HTTP trigger function processed a request.");

            dynamic data;
            try
            {
                data = JsonConvert.DeserializeObject(new StreamReader(req.Body).ReadToEnd());
            }
            catch (Exception ex)
            {
                return Helpers.Helpers.ReturnErrorException(log, ex);
            }

            var inputFileName = (string)data.inputFileName;
            var storageAccount = (string)data.storageAccount;
            var storageKey = (string)data.storageKey;
            var outputAccount = (string)data.outputAccount;
            var outputKey = (string)data.outputKey;

            StorageCredentials inpoutStorageCredentials = new StorageCredentials(storageAccount, storageKey);
            CloudStorageAccount inputStorageAccount = new CloudStorageAccount(inpoutStorageCredentials, useHttps: true);

            StorageCredentials outputStorageCredentials = new StorageCredentials(outputAccount, outputKey);
            CloudStorageAccount outputStorageAccount = new CloudStorageAccount(outputStorageCredentials, useHttps: true);

            CloudBlobClient outputBlobClient = outputStorageAccount.CreateCloudBlobClient();
            CloudBlobContainer outputContainer = outputBlobClient.GetContainerReference("pre-agora");

            CloudBlobClient inputBlobClient = inputStorageAccount.CreateCloudBlobClient();
            CloudBlobContainer inputContainer = inputBlobClient.GetContainerReference("upload-agora");

            var blobs = await ListBlobsAsync(inputContainer);
            var shortName = inputFileName.Replace(".m3u8", "");
            var selectedBlobs = blobs.Cast<CloudBlockBlob>().Where(a=>a.Name.Contains(shortName)).Select(a=>a.Name).ToList();

            try
            {
                var folder = context.FunctionDirectory;
                var tempFolder = Path.GetTempPath();

                string pathLocalInput = System.IO.Path.Combine(tempFolder, inputFileName);

                string outputFileName = Guid.NewGuid().ToString()+".mp4";
                string pathLocalOutput = System.IO.Path.Combine(tempFolder, outputFileName);

                foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    log.LogInformation($"{drive.Name}: {drive.TotalFreeSpace / 1024 / 1024} MB");
                }

                foreach (var name in selectedBlobs) {
                    string localPath = System.IO.Path.Combine(tempFolder, name);

                    /* Downloads the original video file from blob to local storage. */
                    log.LogInformation("Dowloading source files from blob to local");
                    using (FileStream fs = System.IO.File.Create(localPath))
                    {
                        try
                        {
                            var readBlob = inputContainer.GetBlockBlobReference(name);
                            await readBlob.DownloadToStreamAsync(fs);
                            log.LogInformation("Downloaded input file from blob" + name);
                        }
                        catch (Exception ex)
                        {
                            log.LogError("There was a problem downloading input file from blob. " + ex.ToString());
                        }
                    }
                }

                log.LogInformation("Encoding...");

                string ffmepegpath = System.IO.Path.Combine(tempFolder, "ffmpeg.exe");
                log.LogInformation($"ffmeg Exists: {File.Exists(ffmepegpath)}");

                if (!File.Exists(ffmepegpath)) { 
                var net = new System.Net.WebClient();
                WebClient wc = new WebClient();
                wc.DownloadFile(new Uri("https://www.dropbox.com/s/3nt5ymir27hcs68/ffmpeg.exe?dl=0"), ffmepegpath);

                }

                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.WorkingDirectory = tempFolder;
                process.StartInfo.FileName = ffmepegpath;

                log.LogInformation($"ffmeg Exists: {File.Exists(ffmepegpath)}");

                log.LogInformation($"Temp In Exists: {File.Exists(pathLocalInput)}");


                process.StartInfo.Arguments = ("-i {input} -acodec copy -bsf:a aac_adtstoasc -vcodec copy {output}")
                    .Replace("{input}", "\"" + pathLocalInput + "\"")
                    .Replace("{output}", "\"" + pathLocalOutput + "\"")
                    .Replace("'", "\"");

                log.LogInformation(process.StartInfo.Arguments);
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                var sb = new StringBuilder();
                process.OutputDataReceived += new DataReceivedEventHandler(
                    (s, e) =>
                    {
                        log.LogInformation("O: " + e.Data);
                    }
                );
                process.ErrorDataReceived += (s, a) => sb.AppendLine(a.Data);
                process.EnableRaisingEvents = true;

                //start process
                process.Start();
                log.LogInformation("process started");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    log.LogInformation(sb.ToString());
                    throw new InvalidOperationException($"Ffmpeg failed with exit code {process.ExitCode}");
                }

                exitCode = process.ExitCode;
                ffmpegResult = output;


                log.LogInformation("Video Converted");

                /* Uploads the encoded video file from local to blob. */
                log.LogInformation("Uploading encoded file to blob");

                log.LogInformation($"Temp Out Exists: {File.Exists(pathLocalOutput)}");

                using (FileStream fs = System.IO.File.OpenRead(pathLocalOutput))
                {
                    try
                    {
                        var writeBlob = outputContainer.GetBlockBlobReference(outputFileName);
                        await writeBlob.UploadFromStreamAsync(fs);
                        log.LogInformation("Uploaded encoded file to blob");
                    }
                    catch (Exception ex)
                    {
                        log.LogInformation("There was a problem uploading converted file to blob. " + ex.ToString());
                    }
                }

                var files = Directory.GetFiles(tempFolder, "*.*", SearchOption.AllDirectories);
                foreach (var fll in files)
                {
                    if (File.Exists(fll))
                    {
                        File.Delete(fll);
                    }
                }

                System.IO.File.Delete(pathLocalOutput);
            }
            catch (Exception e)
            {
                isSuccessful = false;
                errorText += e.Message;
            }

            if (exitCode != 0)
            {
                isSuccessful = false;
            }

            var response = new JObject
            {
                {"isSuccessful", isSuccessful},
                {"ffmpegResult",  ffmpegResult},
                {"errorText", errorText }

            };

            return new OkObjectResult(
                response
            );
        }
    }
}