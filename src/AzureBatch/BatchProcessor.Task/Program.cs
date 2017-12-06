using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace BatchProcessorTask
{
    public class Program
    {
        private static string StorageConnectionString;
        private static string StorageInputContainerName;
        private static string StorageOutputContainerName;

        private static CloudBlobClient StorageClient;

        static void Main(string[] args)
        {
            StorageConnectionString = ConfigurationManager.AppSettings["AzureStorageConnectionString"];
            StorageInputContainerName = ConfigurationManager.AppSettings["StorageInputContainerName"];
            StorageOutputContainerName = ConfigurationManager.AppSettings["StorageOutputContainerName"];

            string commandline = args[0];

            if (String.IsNullOrEmpty(StorageConnectionString) || String.IsNullOrEmpty(StorageInputContainerName) || String.IsNullOrEmpty(StorageOutputContainerName) ||
                String.IsNullOrEmpty(commandline))
            {
                throw new InvalidOperationException("One ore more account credential strings have not been populated. Please ensure that all relevant parameters have been specified.");
            }

            try
            {
                // Call the asynchronous version of the Main() method. This is done so that we can await various
                // calls to async methods within the "Main" method of this console application.
                MainAsync(commandline).Wait();
            }
            catch (AggregateException ae)
            {
                Console.WriteLine();
                Console.WriteLine("One or more exceptions occurred.");
                Console.WriteLine();

                PrintAggregateException(ae);
            }
        }

        /// <summary>
        /// Provides an asynchronous version of the Main method, allowing for the awaiting of async method calls within.
        /// </summary>
        /// <returns>A <see cref="System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
        private static async Task MainAsync(string commandline)
        {
            Console.WriteLine("Entering reducer task");
            
            // Retrieve the storage account and create client
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(StorageConnectionString);
            StorageClient = storageAccount.CreateCloudBlobClient();

            //// get the input files
            await DownloadBlobsAsync(StorageInputContainerName);

            // run the commandline
            ExecuteCommand(commandline);

            Console.WriteLine("Finishing reducer task");
        }

        private static void ExecuteCommand(string command)
        {
            Console.Write($"Beginning executing reducer command '{command}' ...");

            var cmdsi = new ProcessStartInfo()
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                FileName = "cmd.exe",
                Arguments = $"{command}"
            };

            var process = new Process
            {
                StartInfo = cmdsi
            };
            process.OutputDataReceived += (sender, args) => Console.WriteLine(args.Data);
            process.ErrorDataReceived += (sender, args) => Console.WriteLine(args.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();
            process.Close();

            Console.WriteLine("Done");
        }

        static async Task DownloadBlobsAsync(string containerName)
        {
            var blobContainer = StorageClient.GetContainerReference(containerName);

            // Get a flat listing of all the block blobs in the specified container
            foreach (IListBlobItem item in blobContainer.ListBlobs(prefix: null, useFlatBlobListing: true))
            {
                // Retrieve reference to the current blob
                CloudBlob blob = (CloudBlob)item;

                Console.Write($"Downloading blob '{blob.Name}' ...");
                // Save blob contents to a file in the specified folder
                await blob.DownloadToFileAsync(blob.Name, FileMode.Create);
                Console.WriteLine("Done");
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
