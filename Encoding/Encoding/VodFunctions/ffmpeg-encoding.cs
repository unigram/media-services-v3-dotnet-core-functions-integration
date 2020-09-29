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
        public static async Task<IActionResult> EncodeAgora(
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
            var inputUrl = (string)data.inputFileUrl;

            var outputAccount = (string)data.outputAccount;
            var outputKey = (string)data.outputKey;

        
            StorageCredentials outputStorageCredentials = new StorageCredentials(outputAccount, outputKey);
            CloudStorageAccount outputStorageAccount = new CloudStorageAccount(outputStorageCredentials, useHttps: true);

            CloudBlobClient outputBlobClient = outputStorageAccount.CreateCloudBlobClient();
            CloudBlobContainer outputContainer = outputBlobClient.GetContainerReference("vod-upload");

            var shortName = inputFileName.Replace(".m3u8", "");

            try
            {
                var folder = context.FunctionDirectory;
                var tempFolder = Path.GetTempPath();

                string outputFileName = shortName + ".mp4";
                string pathLocalOutput = System.IO.Path.Combine(tempFolder, outputFileName);

                foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    log.LogInformation($"{drive.Name}: {drive.TotalFreeSpace / 1024 / 1024} MB");
                }

                log.LogInformation("Encoding...");
                var file = System.IO.Path.Combine(folder, "..\\ffmpeg\\ffmpeg.exe");
                log.LogInformation($"ffmeg Exists: {File.Exists(file)}");

                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.WorkingDirectory = tempFolder;
                process.StartInfo.FileName = file;

                process.StartInfo.Arguments = ("-i {input} -acodec copy -bsf:a aac_adtstoasc -vcodec copy {output}")
                    .Replace("{input}", "\"" + inputUrl + "\"")
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

                if (File.Exists(pathLocalOutput))
                {
                    File.Delete(pathLocalOutput);
                }

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

        [FunctionName("ffmpeg-encoding-download")]
        public static async Task<IActionResult> EncodeDownload(
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
            var shortName = inputFileName.Replace(".mp4", "");

            StorageCredentials inpoutStorageCredentials = new StorageCredentials(storageAccount, storageKey);
            CloudStorageAccount inputStorageAccount = new CloudStorageAccount(inpoutStorageCredentials, useHttps: true);

            StorageCredentials outputStorageCredentials = new StorageCredentials(outputAccount, outputKey);
            CloudStorageAccount outputStorageAccount = new CloudStorageAccount(outputStorageCredentials, useHttps: true);

            CloudBlobClient outputBlobClient = outputStorageAccount.CreateCloudBlobClient();
            CloudBlobContainer outputContainer = outputBlobClient.GetContainerReference("download");

            CloudBlobClient inputBlobClient = inputStorageAccount.CreateCloudBlobClient();
            CloudBlobContainer inputContainer = inputBlobClient.GetContainerReference("vod-upload");


            var folder = context.FunctionDirectory;
            var tempFolder = Path.GetTempPath();

            string pathLocalInput = System.IO.Path.Combine(tempFolder, inputFileName);

            string outputFileName = shortName + "_download.mp4";
            string pathLocalOutput = System.IO.Path.Combine(tempFolder, outputFileName);

            try
            {
             

                foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    log.LogInformation($"{drive.Name}: {drive.TotalFreeSpace / 1024 / 1024} MB");
                }

                log.LogInformation("Downloading...");
                var blockBlob = inputContainer.GetBlockBlobReference(inputFileName);
                await blockBlob.DownloadToFileAsync(pathLocalInput, FileMode.OpenOrCreate);

                log.LogInformation("Encoding...");
                var file = System.IO.Path.Combine(folder, "..\\ffmpeg\\ffmpeg.exe");
                var fileLogo = System.IO.Path.Combine(folder, "..\\ffmpeg\\logowhite.png");

                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.WorkingDirectory = tempFolder;
                process.StartInfo.FileName = file;

                log.LogInformation($"Temp In Exists: {File.Exists(pathLocalInput)}");


                process.StartInfo.Arguments = ("-y -i {input} -i {logoinput} -filter_complex \"overlay=x=main_w*0.01:y=main_h*0.01\" {output}")
                    .Replace("{input}", "\"" + pathLocalInput + "\"")
                    .Replace("{output}", "\"" + pathLocalOutput + "\"")
                    .Replace("{logoinput}", "\"" + fileLogo + "\"")
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

                if (File.Exists(pathLocalInput))
                {
                    File.Delete(pathLocalInput);
                }

                if (File.Exists(pathLocalOutput))
                {
                    File.Delete(pathLocalOutput);
                }

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

        [FunctionName("ffmpeg-encoding-podcast")]
        public static async Task<IActionResult> EncodePodcast(
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
            var shortName = inputFileName.Replace(".mp4", "");

            StorageCredentials inpoutStorageCredentials = new StorageCredentials(storageAccount, storageKey);
            CloudStorageAccount inputStorageAccount = new CloudStorageAccount(inpoutStorageCredentials, useHttps: true);

            StorageCredentials outputStorageCredentials = new StorageCredentials(outputAccount, outputKey);
            CloudStorageAccount outputStorageAccount = new CloudStorageAccount(outputStorageCredentials, useHttps: true);

            CloudBlobClient outputBlobClient = outputStorageAccount.CreateCloudBlobClient();
            CloudBlobContainer outputContainer = outputBlobClient.GetContainerReference("podcast");

            CloudBlobClient inputBlobClient = inputStorageAccount.CreateCloudBlobClient();
            CloudBlobContainer inputContainer = inputBlobClient.GetContainerReference("vod-upload");


            var folder = context.FunctionDirectory;
            var tempFolder = Path.GetTempPath();

            string pathLocalInput = System.IO.Path.Combine(tempFolder, inputFileName);

            string outputFileName = shortName + "_podcast.mp3";
            string pathLocalOutput = System.IO.Path.Combine(tempFolder, outputFileName);

            try
            {


                foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    log.LogInformation($"{drive.Name}: {drive.TotalFreeSpace / 1024 / 1024} MB");
                }

                log.LogInformation("Downloading...");
                var blockBlob = inputContainer.GetBlockBlobReference(inputFileName);
                await blockBlob.DownloadToFileAsync(pathLocalInput, FileMode.OpenOrCreate);

                log.LogInformation("Encoding...");
                var file = System.IO.Path.Combine(folder, "..\\ffmpeg\\ffmpeg.exe");

                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.WorkingDirectory = tempFolder;
                process.StartInfo.FileName = file;

                log.LogInformation($"Temp In Exists: {File.Exists(pathLocalInput)}");


                process.StartInfo.Arguments = ("-i {input} -b:a 192K -vn {output}")
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

                if (File.Exists(pathLocalInput))
                {
                    File.Delete(pathLocalInput);
                }

                if (File.Exists(pathLocalOutput))
                {
                    File.Delete(pathLocalOutput);
                }
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