
// Copyright (c) Microsoft Corporation
//
// Companion project to the following article:
// https://azure.microsoft.com/documentation/articles/batch-dotnet-get-started/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.Batch;
using Microsoft.Azure.Batch.Auth;
using Microsoft.Azure.Batch.Common;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Configuration;

namespace BatchProcessor
{
    public class Program
    {
        private static string BatchAccountName;
        private static string BatchAccountKey;
        private static string BatchAccountUrl;

        private static string StorageConnectionString;
        private static string StorageInputContainerName;
        private static string StorageOutputContainerName;

        private static string BatchPoolId;
        private static string BatchJobId;
        private static int BatchJobTimeoutInMinutes;
        private static string BatchJobTaskMapperCommandline;
        private static string BatchJobTaskReducerCommandline;

        private static string LocalInputDirectory;
        private static string LocalOutputDirectory;

        public static void Main(string[] args)
        {
            BatchAccountName = ConfigurationManager.AppSettings["AzureBatchAccountName"];
            BatchAccountKey = ConfigurationManager.AppSettings["AzureBatchAccountKey"];
            BatchAccountUrl = ConfigurationManager.AppSettings["AzureBatchAccountUrl"];

            StorageConnectionString = ConfigurationManager.AppSettings["AzureStorageConnectionString"];
            StorageInputContainerName = ConfigurationManager.AppSettings["StorageInputContainerName"];
            StorageOutputContainerName = ConfigurationManager.AppSettings["StorageOutputContainerName"];

            BatchPoolId = ConfigurationManager.AppSettings["BatchPoolId"];
            BatchJobId = ConfigurationManager.AppSettings["BatchJobId"];
            BatchJobTimeoutInMinutes = int.Parse(ConfigurationManager.AppSettings["BatchJobTimeoutInMinutes"]);
            BatchJobTaskMapperCommandline = ConfigurationManager.AppSettings["BatchJobTaskMapperCommandline"];
            BatchJobTaskReducerCommandline = ConfigurationManager.AppSettings["BatchJobTaskReducerCommandline"];

            LocalInputDirectory = ConfigurationManager.AppSettings["LocalInputDirectory"];
            LocalOutputDirectory = ConfigurationManager.AppSettings["LocalOutputDirectory"];

            if (String.IsNullOrEmpty(BatchAccountName) || String.IsNullOrEmpty(BatchAccountKey) || String.IsNullOrEmpty(BatchAccountUrl) ||
                String.IsNullOrEmpty(StorageConnectionString) || String.IsNullOrEmpty(StorageInputContainerName) || String.IsNullOrEmpty(StorageOutputContainerName) ||
                String.IsNullOrEmpty(BatchPoolId) || String.IsNullOrEmpty(BatchJobId) || String.IsNullOrEmpty(LocalOutputDirectory))
            {
                throw new InvalidOperationException("One ore more account credential strings have not been populated. Please ensure that all relevant parameters have been specified.");
            }

            try
            {
                // Call the asynchronous version of the Main() method. This is done so that we can await various
                // calls to async methods within the "Main" method of this console application.
                MainAsync().Wait();
            }
            catch (AggregateException ae)
            {
                Console.WriteLine();
                Console.WriteLine("One or more exceptions occurred.");
                Console.WriteLine();

                PrintAggregateException(ae);
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("Sample complete, hit ENTER to exit...");
                Console.ReadLine();
            }
        }

        /// <summary>
        /// Provides an asynchronous version of the Main method, allowing for the awaiting of async method calls within.
        /// </summary>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task MainAsync()
        {
            Console.WriteLine("Sample start: {0}", DateTime.Now);
            Console.WriteLine();
            Stopwatch timer = new Stopwatch();
            timer.Start();

            // Retrieve the storage account
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(StorageConnectionString);

            // Create the blob client, for use in obtaining references to blob storage containers
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

            // Use the blob client to create the containers in Azure Storage if they don't yet exist
            await CreateContainerIfNotExistAsync(blobClient, StorageInputContainerName);
            await CreateContainerIfNotExistAsync(blobClient, StorageOutputContainerName);

            // Upload the data files. This is the data that will be processed by each of the tasks that are
            // executed on the compute nodes within the pool.
            List<ResourceFile> inputFiles = await UploadFilesToContainerAsync(blobClient, StorageInputContainerName, LocalInputDirectory);

            // Obtain a shared access signature that provides write access to the output container to which
            // the tasks will upload their output.
            string outputContainerSasUrl = GetContainerSasUrl(blobClient, StorageOutputContainerName, SharedAccessBlobPermissions.Write | SharedAccessBlobPermissions.Read | SharedAccessBlobPermissions.List);

            // Create a BatchClient. We'll now be interacting with the Batch service in addition to Storage
            BatchSharedKeyCredentials cred = new BatchSharedKeyCredentials(BatchAccountUrl, BatchAccountName, BatchAccountKey);
            using (BatchClient batchClient = BatchClient.Open(cred))
            {
                // Create the job that will run the tasks.
                await CreateJobAsync(batchClient, BatchJobId, BatchPoolId, storageAccount);

                // Add the tasks to the job. We need to supply a container shared access signature for the
                // tasks so that they can upload their output to Azure Storage.
                await AddTasksAsync(batchClient, BatchJobId, inputFiles, outputContainerSasUrl, storageAccount);

                // Monitor task success/failure, specifying a maximum amount of time to wait for the tasks to complete
                await MonitorTasks(batchClient, BatchJobId, TimeSpan.FromMinutes(BatchJobTimeoutInMinutes));

                // Download the task output files from the output Storage container to a local directory
                //await DownloadBlobsFromContainerAsync(blobClient, outputContainerName, Environment.GetEnvironmentVariable("TEMP"));
                await DownloadBlobsFromContainerAsync(blobClient, StorageOutputContainerName, LocalOutputDirectory);

                // Clean up Storage resources
                //await DeleteContainerAsync(blobClient, appContainerName);
                await DeleteContainerAsync(blobClient, StorageInputContainerName);
                await DeleteContainerAsync(blobClient, StorageOutputContainerName);

                // Print out some timing info
                timer.Stop();
                Console.WriteLine();
                Console.WriteLine("Sample end: {0}", DateTime.Now);
                Console.WriteLine("Elapsed time: {0}", timer.Elapsed);

                // Clean up Batch resources (if the user so chooses)
                Console.WriteLine();
                Console.Write("Delete job? [yes] no: ");
                string response = Console.ReadLine().ToLower();
                if (response != "n" && response != "no")
                {
                    await batchClient.JobOperations.DeleteJobAsync(BatchJobId);
                }
            }
        }

        private static List<ResourceFile> RetrieveMapperResults(CloudBlobClient blobClient, string containerName)
        {
            List<ResourceFile> resFiles = new List<ResourceFile>();

            // Retrieve a reference to a previously created container
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);

            // Get a flat listing of all the block blobs in the specified container
            foreach (IListBlobItem item in container.ListBlobs(prefix: null, useFlatBlobListing: true))
            {
                // Retrieve reference to the current blob
                CloudBlob blob = (CloudBlob)item;

                // Set the expiry time and permissions for the blob shared access signature. In this case, no start time is specified,
                // so the shared access signature becomes valid immediately
                SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy
                {
                    SharedAccessExpiryTime = DateTime.UtcNow.AddHours(2),
                    Permissions = SharedAccessBlobPermissions.Read
                };

                // Construct the SAS URL for blob
                string sasBlobToken = blob.GetSharedAccessSignature(sasConstraints);
                string blobSasUri = String.Format("{0}{1}", blob.Uri, sasBlobToken);

                resFiles.Add(new ResourceFile(blobSasUri, blob.Name));
            }

            return resFiles;
        }

        /// <summary>
        /// Creates a container with the specified name in Blob storage, unless a container with that name already exists.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name for the new container.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task CreateContainerIfNotExistAsync(CloudBlobClient blobClient, string containerName)
        {
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);

            if (await container.CreateIfNotExistsAsync())
            {
                Console.WriteLine("Container [{0}] created.", containerName);
            }
            else
            {
                Console.WriteLine("Container [{0}] exists, skipping creation.", containerName);
            }
        }

        /// <summary>
        /// Returns a shared access signature (SAS) URL providing the specified permissions to the specified container.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the container for which a SAS URL should be obtained.</param>
        /// <param name="permissions">The permissions granted by the SAS URL.</param>
        /// <returns>A SAS URL providing the specified access to the container.</returns>
        /// <remarks>The SAS URL provided is valid for 2 hours from the time this method is called. The container must
        /// already exist within Azure Storage.</remarks>
        private static string GetContainerSasUrl(CloudBlobClient blobClient, string containerName, SharedAccessBlobPermissions permissions)
        {
            // Set the expiry time and permissions for the container access signature. In this case, no start time is specified,
            // so the shared access signature becomes valid immediately
            SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy
            {
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(2),
                Permissions = permissions
            };

            // Generate the shared access signature on the container, setting the constraints directly on the signature
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);
            string sasContainerToken = container.GetSharedAccessSignature(sasConstraints);

            // Return the URL string for the container, including the SAS token
            return String.Format("{0}{1}", container.Uri, sasContainerToken);
        }

        /// <summary>
        /// Uploads the specified files to the specified Blob container, returning a corresponding
        /// collection of <see cref="ResourceFile"/> objects appropriate for assigning to a task's
        /// <see cref="CloudTask.ResourceFiles"/> property.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="inputContainerName">The name of the blob storage container to which the files should be uploaded.</param>
        /// <param name="inputFileDirectory">Absolute directory path containing the input files</param>
        /// <returns>A collection of <see cref="ResourceFile"/> objects.</returns>
        private static async Task<List<ResourceFile>> UploadFilesToContainerAsync(CloudBlobClient blobClient, string inputContainerName, string inputFileDirectory)
        {
            List<ResourceFile> resourceFiles = new List<ResourceFile>();

            foreach (string filePath in Directory.EnumerateFiles(inputFileDirectory))
            {
                resourceFiles.Add(await UploadFileToContainerAsync(blobClient, inputContainerName, filePath));
            }

            return resourceFiles;
        }

        /// <summary>
        /// Uploads the specified file to the specified Blob container.
        /// </summary>
        /// <param name="filePath">The full path to the file to upload to Storage.</param>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the blob storage container to which the file should be uploaded.</param>
        /// <returns>A <see cref="Microsoft.Azure.Batch.ResourceFile"/> instance representing the file within blob storage.</returns>
        private static async Task<ResourceFile> UploadFileToContainerAsync(CloudBlobClient blobClient, string containerName, string filePath)
        {
            Console.WriteLine("Uploading file {0} to container [{1}]...", filePath, containerName);

            string blobName = Path.GetFileName(filePath);

            CloudBlobContainer container = blobClient.GetContainerReference(containerName);
            CloudBlockBlob blobData = container.GetBlockBlobReference(blobName);
            await blobData.UploadFromFileAsync(filePath);

            // Set the expiry time and permissions for the blob shared access signature. In this case, no start time is specified,
            // so the shared access signature becomes valid immediately
            SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy
            {
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(2),
                Permissions = SharedAccessBlobPermissions.Read
            };

            // Construct the SAS URL for blob
            string sasBlobToken = blobData.GetSharedAccessSignature(sasConstraints);
            string blobSasUri = String.Format("{0}{1}", blobData.Uri, sasBlobToken);

            return new ResourceFile(blobSasUri, blobName);
        }

        /// <summary>
        /// Creates a job in the specified pool.
        /// </summary>
        /// <param name="batchClient">A <see cref="BatchClient"/>.</param>
        /// <param name="jobId">The id of the job to be created.</param>
        /// <param name="poolId">The id of the <see cref="CloudPool"/> in which to create the job.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task CreateJobAsync(BatchClient batchClient, string jobId, string poolId, CloudStorageAccount linkedStorageAccount)
        {
            Console.WriteLine("Creating job [{0}]...", jobId);

            CloudJob job = batchClient.JobOperations.CreateJob();
            job.Id = jobId;
            job.PoolInformation = new PoolInformation { PoolId = poolId };
            job.UsesTaskDependencies = true;

            await job.CommitAsync();
        }

        /// <summary>
        /// Creates tasks to process each of the specified input files, and submits them to the
        /// specified job for execution.
        /// </summary>
        /// <param name="batchClient">A <see cref="BatchClient"/>.</param>
        /// <param name="jobId">The id of the job to which the tasks should be added.</param>
        /// <param name="inputFiles">A collection of <see cref="ResourceFile"/> objects representing the input files to be
        /// processed by the tasks executed on the compute nodes.</param>
        /// <param name="outputContainerSasUrl">The shared access signature URL for the container within Azure Storage that
        /// will receive the output files created by the tasks.</param>
        /// <returns>A collection of the submitted tasks.</returns>
        private static async Task<List<CloudTask>> AddTasksAsync(BatchClient batchClient, string jobId, List<ResourceFile> inputFiles, string outputContainerSasUrl, CloudStorageAccount linkedStorageAccount)
        {
            Console.WriteLine("Adding {0} tasks to job [{1}]...", inputFiles.Count, jobId);

            // Create a collection to hold the tasks that we'll be adding to the job
            List<CloudTask> tasks = new List<CloudTask>();

            // Create each of the tasks. Because we copied the task application to the
            // node's shared directory with the pool's StartTask, we can access it via
            // the shared directory on whichever node each task will run.
            foreach (ResourceFile inputFile in inputFiles)
            {
                string taskId = $"task-mapper-{inputFiles.IndexOf(inputFile)}";
                string mapperTaskCommandLine = $"{BatchJobTaskMapperCommandline} {inputFile.FilePath}";

                CloudTask mapper = new CloudTask(taskId, mapperTaskCommandLine)
                {
                    OutputFiles = new List<OutputFile>
                    {
                        new OutputFile(filePattern: @"dsfinal.txt",
                            destination: new OutputFileDestination(new OutputFileBlobContainerDestination(containerUrl: outputContainerSasUrl, path: $"{taskId}.txt")),
                            uploadOptions: new OutputFileUploadOptions(
                                uploadCondition: OutputFileUploadCondition.TaskCompletion))
                    },
                    ResourceFiles = new List<ResourceFile> { inputFile }
                };
                tasks.Add(mapper);
            }

            string reducerTaskId = "task-reducer";
            string reducerTaskCommandLine = BatchJobTaskReducerCommandline;
            CloudTask reducer = new CloudTask(reducerTaskId, reducerTaskCommandLine)
            {
                DependsOn = TaskDependencies.OnTasks(tasks),
                OutputFiles = new List<OutputFile>
                    {
                        new OutputFile(filePattern: @"*.txt",
                            destination: new OutputFileDestination(new OutputFileBlobContainerDestination(containerUrl: outputContainerSasUrl, path: reducerTaskId)),
                            uploadOptions: new OutputFileUploadOptions(
                                uploadCondition: OutputFileUploadCondition.TaskCompletion))
                    }
            };

            var reducerInputFiles = new List<ResourceFile>();
            // define the input files for the reducer task
            foreach (var maptask in tasks)
            {
                foreach (var outFile in maptask.OutputFiles)
                {
                    // ensure that the container SAS has the following rights: write|read|list - otherwise we can't download the files
                    var sasContainerUrl = outFile.Destination.Container.ContainerUrl;
                    var blobName = outFile.Destination.Container.Path;
                    var sasBlob = sasContainerUrl.Insert(sasContainerUrl.IndexOf('?'), $"/{blobName}");
                    reducerInputFiles.Add(new ResourceFile(sasBlob, blobName));
                }
            }
            reducer.ResourceFiles = reducerInputFiles;

            tasks.Add(reducer);

            // Add the tasks as a collection opposed to a separate AddTask call for each. Bulk task submission
            // helps to ensure efficient underlying API calls to the Batch service.
            await batchClient.JobOperations.AddTaskAsync(jobId, tasks);

            return tasks;
        }

        /// <summary>
        /// Monitors the specified tasks for completion and returns a value indicating whether all tasks completed successfully
        /// within the timeout period.
        /// </summary>
        /// <param name="batchClient">A <see cref="BatchClient"/>.</param>
        /// <param name="jobId">The id of the job containing the tasks that should be monitored.</param>
        /// <param name="timeout">The period of time to wait for the tasks to reach the completed state.</param>
        /// <returns><c>true</c> if all tasks in the specified job completed with an exit code of 0 within the specified timeout period, otherwise <c>false</c>.</returns>
        private static async Task<bool> MonitorTasks(BatchClient batchClient, string jobId, TimeSpan timeout)
        {
            bool allTasksSuccessful = true;
            const string successMessage = "All tasks reached state Completed.";
            const string failureMessage = "One or more tasks failed to reach the Completed state within the timeout period.";

            // Obtain the collection of tasks currently managed by the job. Note that we use a detail level to
            // specify that only the "id" property of each task should be populated. Using a detail level for
            // all list operations helps to lower response time from the Batch service.
            ODATADetailLevel detail = new ODATADetailLevel(selectClause: "id");
            List<CloudTask> tasks = await batchClient.JobOperations.ListTasks(BatchJobId, detail).ToListAsync();

            Console.WriteLine("Awaiting task completion, timeout in {0}...", timeout.ToString());

            // We use a TaskStateMonitor to monitor the state of our tasks. In this case, we will wait for all tasks to
            // reach the Completed state.
            TaskStateMonitor taskStateMonitor = batchClient.Utilities.CreateTaskStateMonitor();
            try
            {
                await taskStateMonitor.WhenAll(tasks, TaskState.Completed, timeout);
            }
            catch (TimeoutException)
            {
                await batchClient.JobOperations.TerminateJobAsync(jobId, failureMessage);
                Console.WriteLine(failureMessage);
                return false;
            }

            await batchClient.JobOperations.TerminateJobAsync(jobId, successMessage);

            // All tasks have reached the "Completed" state, however, this does not guarantee all tasks completed successfully.
            // Here we further check each task's ExecutionInfo property to ensure that it did not encounter a scheduling error
            // or return a non-zero exit code.

            // Update the detail level to populate only the task id and executionInfo properties.
            // We refresh the tasks below, and need only this information for each task.
            detail.SelectClause = "id, executionInfo";

            foreach (CloudTask task in tasks)
            {
                // Populate the task's properties with the latest info from the Batch service
                await task.RefreshAsync(detail);

                if (task.ExecutionInformation.Result == TaskExecutionResult.Failure)
                {
                    // A task with failure information set indicates there was a problem with the task. It is important to note that
                    // the task's state can be "Completed," yet still have encountered a failure.

                    allTasksSuccessful = false;

                    Console.WriteLine("WARNING: Task [{0}] encountered a failure: {1}", task.Id, task.ExecutionInformation.FailureInformation.Message);
                    if (task.ExecutionInformation.ExitCode != 0)
                    {
                        // A non-zero exit code may indicate that the application executed by the task encountered an error
                        // during execution. As not every application returns non-zero on failure by default (e.g. robocopy),
                        // your implementation of error checking may differ from this example.

                        Console.WriteLine("WARNING: Task [{0}] returned a non-zero exit code - this may indicate task execution or completion failure.", task.Id);
                    }
                }
            }

            if (allTasksSuccessful)
            {
                Console.WriteLine("Success! All tasks completed successfully within the specified timeout period.");
            }

            return allTasksSuccessful;
        }

        /// <summary>
        /// Downloads all files from the specified blob storage container to the specified directory.
        /// </summary>
        /// <param name="blobClient">A <see cref="CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the blob storage container containing the files to download.</param>
        /// <param name="directoryPath">The full path of the local directory to which the files should be downloaded.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task DownloadBlobsFromContainerAsync(CloudBlobClient blobClient, string containerName, string directoryPath)
        {
            Console.WriteLine("Downloading all files from container [{0}]...", containerName);

            // Retrieve a reference to a previously created container
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);

            // Get a flat listing of all the block blobs in the specified container
            foreach (IListBlobItem item in container.ListBlobs(prefix: null, useFlatBlobListing: true))
            {
                // Retrieve reference to the current blob
                CloudBlob blob = (CloudBlob)item;

                // Save blob contents to a file in the specified folder
                string localOutputFile = Path.Combine(directoryPath, blob.Name);
                await blob.DownloadToFileAsync(localOutputFile, FileMode.Create);
            }

            Console.WriteLine("All files downloaded to {0}", directoryPath);
        }

        /// <summary>
        /// Deletes the container with the specified name from Blob storage, unless a container with that name does not exist.
        /// </summary>
        /// <param name="blobClient">A <see cref="Microsoft.WindowsAzure.Storage.Blob.CloudBlobClient"/>.</param>
        /// <param name="containerName">The name of the container to delete.</param>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task DeleteContainerAsync(CloudBlobClient blobClient, string containerName)
        {
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);

            if (await container.DeleteIfExistsAsync())
            {
                Console.WriteLine("Container [{0}] deleted.", containerName);
            }
            else
            {
                Console.WriteLine("Container [{0}] does not exist, skipping deletion.", containerName);
            }
        }

        /// <summary>
        /// Processes all exceptions inside an <see cref="AggregateException"/> and writes each inner exception to the console.
        /// </summary>
        /// <param name="aggregateException">The <see cref="AggregateException"/> to process.</param>
        public static void PrintAggregateException(AggregateException aggregateException)
        {
            // Flatten the aggregate and iterate over its inner exceptions, printing each
            foreach (Exception exception in aggregateException.Flatten().InnerExceptions)
            {
                Console.WriteLine(exception.ToString());
                Console.WriteLine();
            }
        }
    }
}
