using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;

namespace mock_storage_batch
{
    public static class StorageBatches
    {
        public class BatchContext
        {
            public string FolderName { get; set; }
            public string CustomerId { get; set; }
            public string BatchId { get; set; }
            public string[] RequiredFiles { get; set; }
        }

        public static class KnownRuntimeStatuses
        {
            public const string Running = "Running";
            public const string Completed = "Completed";
        }
        public static class EventNames
        {
            public static string NewFile(string filename) => $"NewFile_{filename}";
        }

        [FunctionName("StorageBatches")]
        public static async Task<string> RunOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContext context,
            TraceWriter log)
        {
            var batchContext = context.GetInput<BatchContext>();
            log.Info($"Starting: {batchContext.BatchId} (folder: {batchContext.FolderName})");


            // Wait for events for all required files
            //var requiredFileTasks = batchContext.RequiredFiles
            //                                .Select(f => context.WaitForExternalEvent<object>(EventNames.NewFile(f)))
            //                                .ToArray();
            //await Task.WhenAll(requiredFileTasks);
            foreach (var file in batchContext.RequiredFiles)
            {
                await context.WaitForExternalEvent<object>(EventNames.NewFile(file));
            }


            // Currently process all files in a single activity function
            // If each file can be processed independently then could split into multiple activity invocations
            await context.CallActivityAsync("ProcessFiles", batchContext);

            log.Info($"Done: {batchContext.BatchId} (folder: {batchContext.FolderName})");
            return batchContext.BatchId;
        }

        [FunctionName("ProcessFiles")]
        public static void ProcessFiles(
            [ActivityTrigger]
            BatchContext batchContext,
            TraceWriter log)
        {
            log.Info($"*** ProcessFiles {batchContext.FolderName} - starting...");

            // TODO - insert real processing here
            System.Threading.Thread.Sleep(2500);

            log.Info($"*** ProcessFiles {batchContext.FolderName} - done, cleaning up....");

            foreach (var filename in batchContext.RequiredFiles)
            {
                // TODO - replace local file access with blob access
                var path = Path.Combine(batchContext.FolderName, $"{batchContext.BatchId}_{filename}");
                if (File.Exists(path))
                {
                    log.Verbose($"Deleting {path}");
                    File.Delete(path);
                }
                else
                {
                    log.Error($"Missing file {path}"); // shouldn't hit this!
                }
            }

            log.Info($"*** ProcessFiles {batchContext.FolderName} - done");
        }

        [FunctionName("StorageBatches_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            // TODO - replace HttpTrigger with eventgrid trigger for blobs
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            TraceWriter log)
        {
            // Determine path
            var query = req.RequestUri.ParseQueryString();
            var path = query["path"];
            if (string.IsNullOrEmpty(path))
            {
                log.Error("path querystring value missing");
                return req.CreateResponse(HttpStatusCode.BadRequest, "path querystring value missing", new JsonMediaTypeFormatter());
            }

            // Get context (required files, instance id, ...)
            var batchContext = GetBatchContextFromPath(path);
            var instanceId = $"instance-{batchContext.BatchId}";

            // Find or start an orchestration instance
            log.Info($"Looking up instance: {instanceId}");
            var status = await starter.GetStatusAsync(instanceId);
            if (status == null)
            {
                log.Info($"no instance found - {instanceId} - starting...");
                await starter.StartNewAsync("StorageBatches", instanceId, batchContext);
                log.Info($"Started orchestration with ID = '{instanceId}'.");

                System.Threading.Thread.Sleep(5000); // TODO - investigate the error that occurs if we remove this
            }
            else
            {
                log.Info($"Got existing instance for {instanceId} (name {status.Name}). status {status.RuntimeStatus})");
            }

            // Raise events for files files that are already in place
            log.Info($"{instanceId}: Raising events for files that exist");
            foreach (var requiredFile in batchContext.RequiredFiles)
            {
                await RaiseEventIfFileExists(starter, log, instanceId, batchContext.FolderName, batchContext.BatchId, requiredFile);
            }

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
        private static async Task RaiseEventIfFileExists(DurableOrchestrationClient starter, TraceWriter log, string instanceId, string folderName, string batchId, string filename)
        {
            var concatenatedFilename = $"{batchId}_{filename}";
            if (File.Exists(Path.Combine(folderName, concatenatedFilename)))
            {
                log.Info($"*** file {concatenatedFilename} for batch {batchId} - found");
                await starter.RaiseEventAsync(instanceId, EventNames.NewFile(filename), null);
            }
            else
            {
                log.Info($"*** file {concatenatedFilename} for batch {batchId} - missing");
            }
        }

        /// <summary>
        /// Generate a base BatchContext from a trigger path
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static BatchContext GetBatchContextFromPath(string path)
        {
            var folderName = Path.GetDirectoryName(path);
            var filename = Path.GetFileName(path);

            // TODO error handling ;-)
            var customerId = filename.Substring(0, filename.IndexOf('_'));
            var batchId = filename.Substring(0, filename.LastIndexOf('_'));
            var filesToWaitFor = GetRequiredFilesForCustomer(customerId);

            if (filesToWaitFor == null)
            {
                throw new Exception($"Customer {customerId} not found");
            }

            return new BatchContext
            {
                FolderName = folderName,
                CustomerId = customerId,
                BatchId = batchId,
                RequiredFiles = filesToWaitFor
            };
        }

        public static string[] GetRequiredFilesForCustomer(string customerId)
        {
            // TODO - look this up in a database
            switch (customerId)
            {
                case "cust1":
                    return new[] { "file1.txt", "file2.txt", "file3.txt", "file4.txt" };
                case "cust2":
                    return new[] { "file1.txt", "file2.txt" };
                case "cust3":
                    return new[] { "file1.txt", "file2.txt", "file3.txt", "file4.txt", "file5.txt", "file6.txt", "file7.txt", "file8.txt", "file9.txt" };
                default:
                    return null;
            }
        }
    }
}