using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using CDMUtil.Context.ADLS;
using CDMUtil.Context.ObjectDefinitions;
using CDMUtil.Manifest;
using System;
using CDMUtil.SQL;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Azure.EventGrid.Models;

namespace CDMUtil
{
    public static class CDMUtilWriter
    {
        [FunctionName("getManifestDefinition")]
        public static async Task<IActionResult> getManifestDefinition(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
        ILogger log, ExecutionContext context)
        {
            log.LogInformation("getManifestDefinition request started");

            string tableList = req.Headers["TableList"];

            var path = System.IO.Path.Combine(context.FunctionDirectory, "..\\Manifest\\Artifacts.json");

            var mds = await ManifestWriter.getManifestDefinition(path, tableList);

            return new OkObjectResult(JsonConvert.SerializeObject(mds));

        }
        [FunctionName("manifestToModelJson")]
        public static async Task<IActionResult> manifestToModelJson(
          [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
          ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            //get data from 
            string tenantId = req.Headers["TenantId"];
            string storageAccount = req.Headers["StorageAccount"];
            string rootFolder = req.Headers["RootFolder"];
            string localFolder = req.Headers["ManifestLocation"];
            string manifestName = req.Headers["ManifestName"];

            AdlsContext adlsContext = new AdlsContext()
            {
                StorageAccount = storageAccount,
                FileSytemName = rootFolder,
                MSIAuth = true,
                TenantId = tenantId
            };

            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");

            ManifestWriter manifestHandler = new ManifestWriter(adlsContext, localFolder);

            bool created = await manifestHandler.manifestToModelJson(adlsContext, manifestName, localFolder);

            return new OkObjectResult("{\"Status\":" + created + "}");
        }
        [FunctionName("createManifest")]
        public static async Task<IActionResult> createManifest(
          [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
          ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");
            //get data from 
            string tenantId = req.Headers["TenantId"];
            string storageAccount = req.Headers["StorageAccount"];
            string rootFolder = req.Headers["RootFolder"];
            string localFolder = req.Headers["LocalFolder"];
            string createModelJson = req.Headers["CreateModelJson"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            EntityList entityList = JsonConvert.DeserializeObject<EntityList>(requestBody);

            AdlsContext adlsContext = new AdlsContext()
            {
                StorageAccount = storageAccount,
                FileSytemName = rootFolder,
                MSIAuth = true,
                TenantId = tenantId
            };

            ManifestWriter manifestHandler = new ManifestWriter(adlsContext, localFolder);
            bool createModel = false;
            if (createModelJson != null && createModelJson.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                createModel = true;
            }

            bool ManifestCreated = await manifestHandler.createManifest(entityList, createModel);

            //Folder structure Tables/AccountReceivable/Group
            var subFolders = localFolder.Split('/');
            string localFolderPath = "";

            for (int i = 0; i < subFolders.Length - 1; i++)
            {
                var currentFolder = subFolders[i];
                var nextFolder = subFolders[i + 1];
                localFolderPath = $"{localFolderPath}/{currentFolder}";

                ManifestWriter SubManifestHandler = new ManifestWriter(adlsContext, localFolderPath);
                await SubManifestHandler.createSubManifest(currentFolder, nextFolder);
            }

            var status = new ManifestStatus() { ManifestName = entityList.manifestName, IsManifestCreated = ManifestCreated };

            return new OkObjectResult(JsonConvert.SerializeObject(status));
        }

    }
    public static class CDMUtilReader

    {
        [FunctionName("manifestToSQL")]
        public static async Task<IActionResult> manifestToSQL(
          [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
          ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            //get configurations data 
            AppConfigurations c = GetAppConfigurations(req, context);

            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");
            List<SQLMetadata> metadataList = new List<SQLMetadata>();
            await ManifestReader.manifestToSQLMetadata(c, metadataList);
           
            // convert metadata to DDL
            log.Log(LogLevel.Information, "Converting metadata to DDL");
            var statementsList = await SQLHandler.SQLMetadataToDDL(metadataList, c);

            log.Log(LogLevel.Information, "Preparing DB");
            // prep DB 
            if (c.synapseOptions.targetDbConnectionString != null)
            {
                SQLHandler.dbSetup(c.synapseOptions, c.tenantId);
            }
            
            // Execute DDL
            log.Log(LogLevel.Information, "Executing DDL");
            SQLHandler sQLHandler = new SQLHandler(c.synapseOptions.targetDbConnectionString, c.tenantId);
            var statements = new SQLStatements { Statements = statementsList };
            sQLHandler.executeStatements(statements);
                             
            return new OkObjectResult(JsonConvert.SerializeObject(statements));
        }
        
        [FunctionName("manifestToSQLDDL")]
        public static async Task<IActionResult> manifestToSQLDDL(
          [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
          ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");
            
            //get configurations data 
            AppConfigurations c = GetAppConfigurations(req, context);

            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");
            List<SQLMetadata> metadataList = new List<SQLMetadata>();
            await ManifestReader.manifestToSQLMetadata(c, metadataList);

            // convert metadata to DDL
            log.Log(LogLevel.Information, "Converting metadata to DDL");
            var statementsList = await SQLHandler.SQLMetadataToDDL(metadataList, c);

            return new OkObjectResult(JsonConvert.SerializeObject(statementsList));
        }

        [FunctionName("EventGrid_CDMToSynapseView")]
        public static void CDMToSynapseView([EventGridTrigger] EventGridEvent eventGridEvent, ILogger log, ExecutionContext context)
        {
            
            dynamic data = eventGridEvent.Data;
           
            //get configurations data 
            AppConfigurations c = GetAppConfigurations(null, context, eventGridEvent);

            log.LogInformation(eventGridEvent.Data.ToString());
            // Read Manifest metadata
            log.Log(LogLevel.Information, "Reading Manifest metadata");
            List<SQLMetadata> metadataList = new List<SQLMetadata>();
            ManifestReader.manifestToSQLMetadata(c, metadataList);

            // convert metadata to DDL
            log.Log(LogLevel.Information, "Converting metadata to DDL");
            var statementsList = SQLHandler.SQLMetadataToDDL(metadataList, c).Result;

            log.Log(LogLevel.Information, "Preparing DB");
            // prep DB 
            if (c.synapseOptions.targetDbConnectionString != null)
            {
                SQLHandler.dbSetup(c.synapseOptions, c.tenantId);
            }

            // Execute DDL
            log.Log(LogLevel.Information, "Executing DDL");
            SQLHandler sQLHandler = new SQLHandler(c.synapseOptions.targetDbConnectionString, c.tenantId);
            var statements = new SQLStatements { Statements = statementsList };
            sQLHandler.executeStatements(statements);
        }
        public static string getConfigurationValue(HttpRequest req, string token)
        {
            string ConfigValue;
            
            if (req != null && String.IsNullOrEmpty(req.Headers[token]))
            {
                ConfigValue = req.Headers[token];
            }
            else
            {
                ConfigValue = System.Environment.GetEnvironmentVariable(token);
            }
            
            return ConfigValue;
        }
        public static AppConfigurations GetAppConfigurations(HttpRequest req, ExecutionContext context, EventGridEvent eventGridEvent= null)
        {
            
            string ManifestURL;

            if (eventGridEvent != null)
            {
                dynamic eventData = eventGridEvent.Data;
                
                ManifestURL = eventData.url;
            }
            else
            {
                ManifestURL = getConfigurationValue(req, "ManifestURL");
            }
            if (ManifestURL.ToLower().EndsWith("cdm.json") == false)
            {
                throw new Exception($"Invalid manifest URL:{ManifestURL}");
            }

            string tenantId = getConfigurationValue(req, "TenantId");
            string connectionString = getConfigurationValue(req, "SQLEndpoint");
            string DDLType = getConfigurationValue(req, "DDLType");
            
            AppConfigurations AppConfiguration = new AppConfigurations(tenantId, ManifestURL, null, connectionString, DDLType);

            string dataSourceName = getConfigurationValue(req, "DataSourceName");
            if (dataSourceName != null)
                AppConfiguration.synapseOptions.external_data_source = dataSourceName;

            string schema = getConfigurationValue(req, "Schema");
            if (schema != null)
                AppConfiguration.synapseOptions.schema = schema;

            string fileFormat = getConfigurationValue(req, "FileFormat");
            if (fileFormat != null)
                AppConfiguration.synapseOptions.fileFormatName = fileFormat;
            
            string DateTimeAsString = getConfigurationValue(req, "DateTimeAsString");            
            if (DateTimeAsString != null)
                AppConfiguration.synapseOptions.DateTimeAsString = bool.Parse(DateTimeAsString);
            
            string ConvertDateTime = getConfigurationValue(req, "ConvertDateTime");
            if (ConvertDateTime != null)
                AppConfiguration.synapseOptions.ConvertDateTime = bool.Parse(ConvertDateTime);

            string TranslateEnum = getConfigurationValue(req, "TranslateEnum");
            if (TranslateEnum != null)
                AppConfiguration.synapseOptions.TranslateEnum = bool.Parse(TranslateEnum);

            AppConfiguration.SourceColumnProperties = Path.Combine(context.FunctionAppDirectory, "SourceColumnProperties.json");
            AppConfiguration.ReplaceViewSyntax = Path.Combine(context.FunctionAppDirectory, "ReplaceViewSyntax.json");
         
            return AppConfiguration;
        }

         

    }
}
