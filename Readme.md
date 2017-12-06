# Simple Map-Reduce using Azure Batch and task dependencies

## Motivation

Although Azure Batch supports Map-Reduce jobs (there is a sample available called '**TextSearch**' https://github.com/Azure/azure-batch-samples/tree/master/CSharp/TextSearch) it gets complicated quickly (it requires a job manager, splitter, etc). On the other hand Azure Batch supports tasks chaining (by configuring dependencies incl. one-to-many) which ensures a task execution order (managed by the Azure Batch service). This makes it quite simple to develop highly scaleable, processing pipelines

Especially for simple workloads the task dependency version is quite interesting. Imagine the following: you have a list of input files and you need to run a certain task per file - let's call them the 'map' tasks, take the output of all runs and process the outcome in a final step using executing another task - let's call it the 'reduce' task. 

However, it turns out that there are some limitations transferring data between these two task types (bascially, the output of the map tasks to abnd have it an input for the reduce task). And this repo contains one possibility to overcome that challenge. 

> ***Important**: The code is based on Microsofts Azure Batch samples and re-uses major code parts of the **ArticleProjects** project (https://github.com/Azure/azure-batch-samples).* 

## Overview & architecture

The solution is split into the following projects:

- **BatchProcessor**: This commandline application is the main executable and takes care of (input/output) file data transfer as well as Batch job and task creation.

- **BatchProcessor.Task**: This helper console application wraps a commandline process. Additionally, it will donwload the blobs of an configurable Azure Storage blob container to the tasks working directory. This enables to build a Batch task which retrieves the input resource files during runtime (and not when the task is created). 

## Preparation & setup

### Create a Batch pool

#### Create and upload application packages
Create separate application packages containing the executables (one for the map tasks and one for the reduce task) which are required by the map and reduce tasks. Ensure that these application packages will be part of the pool configuration.

#### [Optional] Autoscaling a Batch pool

Azure Batch allows to automatically scale the pool nodes based on a custom logic (code). Below is a sample to update (scale up/down) the node instances based on the pending tasks. 

```csharp
// In this example, the pool size is adjusted based on the number of tasks in the queue. Note that both comments and line breaks are acceptable in formula strings.

// Get pending tasks for the past 15 minutes.
$samples = $ActiveTasks.GetSamplePercent(TimeInterval_Minute * 15);
// If we have fewer than 70 percent data points, we use the last sample point, otherwise we use the maximum of last sample point and the history average.
$tasks = $samples < 70 ? max(0, $ActiveTasks.GetSample(1)) : max( $ActiveTasks.GetSample(1), avg($ActiveTasks.GetSample(TimeInterval_Minute * 15)));
// If number of pending tasks is not 0, set targetVM to pending tasks, otherwise half of current dedicated.
$targetVMs = $tasks > 0 ? $tasks : max(0, $TargetDedicated / 2);
// The pool size is capped at 20, if target VM value is more than that, set it to 20. This value should be adjusted according to your use case.
cappedPoolSize = 5;
$TargetDedicated = max(0, min($targetVMs, cappedPoolSize));
// Set node deallocation mode - keep nodes active only until tasks finish
$NodeDeallocationOption = taskcompletion;
```


## Run it

#### Update App.config
Almost all parts of the application is configurable which allows to change certain settings without re-compiling the solution. The settings are stored in the project's App.config file. Ensure that you update the various config values stored in the app.config files before running the solution.

> ***Note***: *The values of the appSettings have to be a valid XML (which has an impact on certain characters like '&' and '"'). This might be relevant for appSettings like **BatchJobTaskMapperCommandline** or **BatchJobTaskReducerCommandline***

The appSettings **BatchJobTaskMapperCommandline** and **BatchJobTaskReducerCommandline** commandline script that will be executed in the map and reduce tasks. Ensure that you correctly reference the available application packages by updating the environment variables **%AZ_BATCH_APP_PACKAGE_<<NAME_OF_THE_PACKAGE>>%**

> ***Note:** E.g. if your pool has an application package called 'mymaptask.zip' then the correct environment variable is  %AZ_BATCH_APP_PACKAGE_mymaptask%*