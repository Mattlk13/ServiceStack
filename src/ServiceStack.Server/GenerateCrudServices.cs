using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ServiceStack.Auth;
using ServiceStack.Configuration;
using ServiceStack.Data;
using ServiceStack.DataAnnotations;
using ServiceStack.Host;
using ServiceStack.NativeTypes;
using ServiceStack.NativeTypes.CSharp;
using ServiceStack.NativeTypes.Dart;
using ServiceStack.NativeTypes.FSharp;
using ServiceStack.NativeTypes.Java;
using ServiceStack.NativeTypes.Kotlin;
using ServiceStack.NativeTypes.Swift;
using ServiceStack.NativeTypes.TypeScript;
using ServiceStack.NativeTypes.VbNet;
using ServiceStack.OrmLite;
using ServiceStack.Web;

namespace ServiceStack
{
    //TODO: persist AutoCrud
    public class GenerateCrudServices : IGenerateCrudServices
    {
        public List<string> IncludeCrudOperations { get; set; } = new List<string> {
            AutoCrudOperation.Query,
            AutoCrudOperation.Create,
            AutoCrudOperation.Update,
            AutoCrudOperation.Patch,
            AutoCrudOperation.Delete,
        };

        /// <summary>
        /// Generate services 
        /// </summary>
        public List<CreateCrudServices> CreateServices { get; set; } = new List<CreateCrudServices>();
        
        public Action<MetadataTypes, MetadataTypesConfig, IRequest> MetadataTypesFilter { get; set; }
        public Action<MetadataType, IRequest> TypeFilter { get; set; }
        public Action<MetadataOperationType, IRequest> ServiceFilter { get; set; }
        
        public Func<MetadataType, bool> IncludeType { get; set; }
        public Func<MetadataOperationType, bool> IncludeService { get; set; }
        
        public string AccessRole { get; set; } = RoleNames.Admin;
        
        internal ConcurrentDictionary<Tuple<string,string>, DbSchema> CachedDbSchemas { get; } 
            = new ConcurrentDictionary<Tuple<string,string>, DbSchema>();

        public Func<ColumnSchema, IOrmLiteDialectProvider, Type> ResolveColumnType { get; set; }
        
        public Type DefaultResolveColumnType(ColumnSchema column, IOrmLiteDialectProvider dialect)
        {
            var dataType = column.DataType;
            if (dataType == null)
                return null;
            
            if (dataType == typeof(string) && column.ColumnSize == 1)
                return typeof(char);

            if (dataType == typeof(Array))
            {
                return column.DataTypeName switch 
                {
                    "hstore" => typeof(Dictionary<string,string>),
                    "bytea" => typeof(byte[]),
                    "text[]" => typeof(string[]),
                    "int[]" => typeof(int[]),
                    "short[]" => typeof(short[]),
                    "bigint[]" => typeof(long[]),
                    "real[]" => typeof(float[]),
                    "double precision[]" => typeof(double[]),
                    "numeric[]" => typeof(decimal[]),
                    "time[]" => typeof(DateTime[]),
                    "timestamp[]" => typeof(DateTime[]),
                    "timestamptz[]" => typeof(DateTimeOffset[]),
                    "timestamp with time zone[]" => typeof(DateTimeOffset[]),
                    _ => null,
                };
            }

            return dataType;
        }
        
        string Localize(string s) => HostContext.AppHost?.ResolveLocalizedString(s, null) ?? s;

        public Dictionary<Type, string[]> ServiceRoutes { get; set; }
        
        private const string NoSchema = "__noschema";

        public GenerateCrudServices()
        {
            ServiceRoutes = new Dictionary<Type, string[]> {
                { typeof(CrudCodeGenTypesService), new[]
                {
                    "/" + Localize("crud") + "/{Include}/{Lang}",
                } },
                { typeof(CrudTablesService), new[] {
                    "/" + Localize("crud") + "/" + Localize("tables"),
                    "/" + Localize("crud") + "/" + Localize("tables") + "/{Schema}",
                } },
            };
            ResolveColumnType = DefaultResolveColumnType;
        }

        public void Register(IAppHost appHost)
        {
            foreach (var registerService in ServiceRoutes)
            {
                appHost.RegisterService(registerService.Key, registerService.Value);
            }
        }

        static string ResolveRequestType(Dictionary<string, MetadataType> existingTypesMap, MetadataOperationType op, CrudCodeGenTypes requestDto,
            out string modifier)
        {
            Tuple<string, string> splitCrudOperation(string requestType)
            {
                if (requestType.StartsWith(AutoCrudOperation.Query))
                    return new Tuple<string, string>(AutoCrudOperation.Query, requestType.Substring(AutoCrudOperation.Query.Length));
                if (requestType.StartsWith(AutoCrudOperation.Create))
                    return new Tuple<string, string>(AutoCrudOperation.Create, requestType.Substring(AutoCrudOperation.Create.Length));
                if (requestType.StartsWith(AutoCrudOperation.Update))
                    return new Tuple<string, string>(AutoCrudOperation.Update, requestType.Substring(AutoCrudOperation.Update.Length));
                if (requestType.StartsWith(AutoCrudOperation.Patch))
                    return new Tuple<string, string>(AutoCrudOperation.Patch, requestType.Substring(AutoCrudOperation.Patch.Length));
                if (requestType.StartsWith(AutoCrudOperation.Delete))
                    return new Tuple<string, string>(AutoCrudOperation.Delete, requestType.Substring(AutoCrudOperation.Delete.Length));
                if (requestType.StartsWith(AutoCrudOperation.Save))
                    return new Tuple<string, string>(AutoCrudOperation.Save, requestType.Substring(AutoCrudOperation.Save.Length));
                throw new NotSupportedException($"Unknown Crud operation '{requestType}'");
            }

            modifier = null;
            if (existingTypesMap.ContainsKey(op.Request.Name))
            {
                // Cannot use more refined Request DTO, skip...
                if (requestDto.NamedConnection == null && requestDto.Schema == null)
                    return null;
                        
                var crudOp = splitCrudOperation(op.Request.Name);
                var newRequestName = crudOp.Item1 + (modifier = StringUtils.SnakeCaseToPascalCase(requestDto.NamedConnection ?? requestDto.Schema)) + crudOp.Item2;
                if (existingTypesMap.ContainsKey(newRequestName))
                {
                    if (requestDto.NamedConnection == null || requestDto.Schema == null)
                        return null;
                            
                    newRequestName = crudOp.Item1 + (modifier = StringUtils.SnakeCaseToPascalCase(requestDto.Schema)) + crudOp.Item2;
                    if (existingTypesMap.ContainsKey(newRequestName))
                    {
                        newRequestName = crudOp.Item1 + (modifier = StringUtils.SnakeCaseToPascalCase(requestDto.NamedConnection + requestDto.Schema)) + crudOp.Item2;
                        if (existingTypesMap.ContainsKey(newRequestName))
                            return null;
                    }
                }
                return newRequestName;
            }
            return op.Request.Name;
        }

        public List<Type> GenerateMissingServices(AutoQueryFeature feature)
        {
            var types = new List<Type>();

            var assemblyName = new AssemblyName { Name = "tmpCrudAssembly" };
            var dynModule = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run)
                .DefineDynamicModule("tmpCrudModule");

            var metadataTypes = GetMissingTypesToCreate(out var existingMetaTypesMap);
            var generatedTypes = new Dictionary<Tuple<string,string>, Type>();

            var appHost = HostContext.AppHost;
            var dtoTypes = new HashSet<Type>();
            foreach (var dtoType in appHost.Metadata.GetAllDtos())
            {
                dtoTypes.Add(dtoType);
            }
            foreach (var assembly in feature.LoadFromAssemblies)
            {
                try
                {
                    var asmAqTypes = assembly.GetTypes().Where(x =>
                        x.HasInterface(typeof(IQueryDb)) || x.HasInterface(typeof(ICrud)));

                    foreach (var aqType in asmAqTypes)
                    {
                        ServiceMetadata.AddReferencedTypes(dtoTypes, aqType);
                    }
                }
                catch (Exception ex)
                {
                    appHost.NotifyStartupException(ex);
                }
            }
            foreach (var dtoType in dtoTypes)
            {
                generatedTypes[key(dtoType)] = dtoType;
            }

            foreach (var metaType in metadataTypes)
            {
                try 
                { 
                    var newType = CreateOrGetType(dynModule, metaType, metadataTypes, existingMetaTypesMap, generatedTypes);
                    types.Add(newType);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }

            return types;
        }

        private static Type CreateOrGetType(ModuleBuilder dynModule, MetadataTypeName metaTypeRef,
            List<MetadataType> metadataTypes, Dictionary<Tuple<string, string>, MetadataType> existingMetaTypesMap, 
            Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var typeKey = key(metaTypeRef);
            var type = ResolveType(typeKey, generatedTypes);
            if (type != null)
                return type;

            if (existingMetaTypesMap.TryGetValue(typeKey, out var metaType))
                return CreateOrGetType(dynModule, metaType, metadataTypes, existingMetaTypesMap, generatedTypes);

            ThrowCouldNotResolveType(metaTypeRef.Namespace + '.' + metaTypeRef.Name);
            return null;
        }

        private static Type CreateOrGetType(ModuleBuilder dynModule, string typeName,
            List<MetadataType> metadataTypes, Dictionary<Tuple<string, string>, MetadataType> existingMetaTypesMap, 
            Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var type = ResolveType(typeName, generatedTypes);
            if (type != null)
                return type;

            foreach (var entry in existingMetaTypesMap)
            {
                if (entry.Value.Name == typeName)
                    return CreateOrGetType(dynModule, entry.Value, metadataTypes, existingMetaTypesMap, generatedTypes);
            }

            ThrowCouldNotResolveType(typeName);
            return null;
        }

        private static void ThrowCouldNotResolveType(string name) =>
            throw new NotSupportedException($"Could not resolve type '{name}'");

        private static readonly Dictionary<string, Type> ServiceStackModelTypes = new Dictionary<string, Type> {
            { nameof(UserAuth), typeof(UserAuth) },
            { nameof(IUserAuth), typeof(IUserAuth) },
            { nameof(UserAuthDetails), typeof(UserAuthDetails) },
            { nameof(IUserAuthDetails), typeof(IUserAuthDetails) },
            { nameof(AuthUserSession), typeof(AuthUserSession) },
            { nameof(IAuthSession), typeof(IAuthSession) },
        };

        private static Type ResolveType(string typeName, Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            foreach (var entry in generatedTypes)
            {
                if (entry.Key.Item2 == typeName)
                    return entry.Value;
            }
            
            var ssInterfacesType = typeof(IQuery).Assembly.GetTypes().FirstOrDefault(x => x.Name == typeName);
            if (ssInterfacesType != null)
                return ssInterfacesType;

            var ssClientType = typeof(Authenticate).Assembly.GetTypes().FirstOrDefault(x => x.Name == typeName);
            if (ssClientType != null)
                return ssClientType;
            
            if (ServiceStackModelTypes.TryGetValue(typeName, out var ssType))
                return ssType;

            return HostContext.AppHost.ScriptContext.ProtectedMethods.@typeof(typeName);
        }

        private static Type ResolveType(Tuple<string,string> typeKey, Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            if (generatedTypes.TryGetValue(typeKey, out var existingType))
                return existingType;

            var fullTypeName = typeKey.Item1 + "." + typeKey.Item2;
            if (fullTypeName.StartsWith("ServiceStack"))
            {
                var ssInterfacesType = typeof(IQuery).Assembly.GetType(fullTypeName);
                if (ssInterfacesType != null)
                    return ssInterfacesType;

                var ssClientType = typeof(Authenticate).Assembly.GetType(fullTypeName);
                if (ssClientType != null)
                    return ssClientType;
            }

            var appType = Type.GetType(fullTypeName);
            if (appType != null)
                return appType;

            return null;
        }

        private static Type CreateOrGetType(ModuleBuilder dynModule, MetadataType metaType, 
            List<MetadataType> metadataTypes, Dictionary<Tuple<string, string>, MetadataType> existingMetaTypesMap, 
            Dictionary<Tuple<string, string>, Type> generatedTypes)
        {
            var typeKey = key(metaType);
            var existingType = ResolveType(typeKey, generatedTypes);
            if (existingType != null)
                return existingType;

            var baseType = metaType.Inherits != null
                ? CreateOrGetType(dynModule, metaType.Inherits, metadataTypes, existingMetaTypesMap, generatedTypes)
                : null;

            if (baseType?.IsGenericTypeDefinition == true)
            {
                var argTypes = new List<Type>();
                foreach (var typeName in metaType.Inherits.GenericArgs)
                {
                    var argType = CreateOrGetType(dynModule, typeName, metadataTypes, existingMetaTypesMap, generatedTypes);
                    argTypes.Add(argType);
                }
                baseType = baseType.MakeGenericType(argTypes.ToArray());
            }
            
            var typeBuilder = dynModule.DefineType(metaType.Namespace + "." + metaType.Name,
                TypeAttributes.Public | TypeAttributes.Class, baseType);
            
            var newType = typeBuilder.CreateTypeInfo().AsType();
            generatedTypes[key(newType)] = newType;
            return newType;
        }

        internal List<MetadataType> GetMissingTypesToCreate(out Dictionary<Tuple<string, string>, MetadataType> typesMap)
        {
            var existingTypesMap = new Dictionary<string, MetadataType>();
            var existingExactTypesMap = new Dictionary<Tuple<string, string>, MetadataType>();
            var existingTypes = new List<MetadataType>();

            void addType(MetadataType type)
            {
                existingTypes.Add(type);
                existingTypesMap[type.Name] = type;
                existingExactTypesMap[key(type)] = type;
            }

            foreach (var genInstruction in CreateServices)
            {
                var requestDto = genInstruction.ConvertTo<CrudCodeGenTypes>();
                requestDto.Include = "new";
                
                var req = new BasicRequest(requestDto);
                var ret = ResolveMetadataTypes(requestDto, req);
                foreach (var op in ret.Item1.Operations)
                {
                    var newName = ResolveRequestType(existingTypesMap, op, requestDto, out var modifier);
                    if (newName == null)
                        continue;
                    if (op.Request.Name != newName)
                    {
                        op.Request.Name = newName;
                        foreach (var route in op.Request.Routes.Safe())
                        {
                            route.Path = ("/" + modifier + "/" + route.Path.Substring(1)).ToLower();
                        }
                    }

                    addType(op.Request);

                    if (op.Response != null && !existingExactTypesMap.ContainsKey(key(op.Response)))
                        addType(op.Response);
                }

                foreach (var metaType in ret.Item1.Types)
                {
                    if (!existingExactTypesMap.ContainsKey(key(metaType)))
                        addType(metaType);
                }
            }

            var orderedTypes = existingTypes
                .OrderBy(x => x.Namespace)
                .ThenBy(x => x.Name)
                .ToList();

            typesMap = existingExactTypesMap;
            return orderedTypes;
        }

        public DbSchema GetCachedDbSchema(IDbConnectionFactory dbFactory, string schema=null, string namedConnection=null)
        {
            var key = new Tuple<string, string>(schema ?? NoSchema, namedConnection);
            return CachedDbSchemas.GetOrAdd(key, k => {

                var tables = GetTableSchemas(dbFactory, schema, namedConnection);
                return new DbSchema {
                    Schema = schema,
                    NamedConnection = namedConnection,
                    Tables = tables,
                };
            });
        }

        public static List<TableSchema> GetTableSchemas(IDbConnectionFactory dbFactory, string schema=null, string namedConnection=null)
        {
            using var db = namedConnection != null
                ? dbFactory.OpenDbConnection(namedConnection)
                : dbFactory.OpenDbConnection();

            var tables = db.GetTableNames(schema);
            var results = new List<TableSchema>();

            var dialect = db.GetDialectProvider();
            foreach (var table in tables)
            {
                var to = new TableSchema {
                    Name = table,
                };

                try
                {
                    var quotedTable = dialect.GetQuotedTableName(table, schema);
                    to.Columns = db.GetTableColumns($"SELECT * FROM {quotedTable}");
                }
                catch (Exception e)
                {
                    to.ErrorType = e.GetType().Name;
                    to.ErrorMessage = e.Message;
                }

                results.Add(to);
            }

            return results;
        }


        public static string GenerateSourceCode(IRequest req, CrudCodeGenTypes request, 
            MetadataTypesConfig typesConfig, MetadataTypes crudMetadataTypes)
        {
            var metadata = req.Resolve<INativeTypesMetadata>();
            var src = request.Lang switch {
                "csharp" => new CSharpGenerator(typesConfig).GetCode(crudMetadataTypes, req),
                "fsharp" => new FSharpGenerator(typesConfig).GetCode(crudMetadataTypes, req),
                "vbnet" => new VbNetGenerator(typesConfig).GetCode(crudMetadataTypes, req),
                "typescript" => new TypeScriptGenerator(typesConfig).GetCode(crudMetadataTypes, req, metadata),
                "dart" => new DartGenerator(typesConfig).GetCode(crudMetadataTypes, req, metadata),
                "swift" => new SwiftGenerator(typesConfig).GetCode(crudMetadataTypes, req),
                "java" => new JavaGenerator(typesConfig).GetCode(crudMetadataTypes, req, metadata),
                "kotlin" => new KotlinGenerator(typesConfig).GetCode(crudMetadataTypes, req, metadata),
                "typescript.d" => new FSharpGenerator(typesConfig).GetCode(crudMetadataTypes, req),
            };
            return src;
        }

        public static string GenerateSource(IRequest req, CrudCodeGenTypes request)
        {
            var ret = ResolveMetadataTypes(request, req);
            var crudMetadataTypes = ret.Item1;
            var typesConfig = ret.Item2;
            
            if (request.Lang == "typescript")
            {
                typesConfig.MakePropertiesOptional = false;
                typesConfig.ExportAsTypes = true;
            }
            else if (request.Lang == "typescript.d")
            {
                typesConfig.MakePropertiesOptional = true;
            }

            var src = GenerateSourceCode(req, request, typesConfig, crudMetadataTypes);
            return src;
        }

        static Tuple<string, string> key(Type type) => new Tuple<string, string>(type.Namespace, type.Name);
        static Tuple<string, string> key(MetadataType type) => new Tuple<string, string>(type.Namespace, type.Name);
        static Tuple<string, string> key(MetadataTypeName type) => new Tuple<string, string>(type.Namespace, type.Name);
        static Tuple<string, string> keyNs(string ns, string name) => new Tuple<string, string>(ns, name);

        public static Tuple<MetadataTypes, MetadataTypesConfig> ResolveMetadataTypes(CrudCodeGenTypes request, IRequest req)
        {
            if (string.IsNullOrEmpty(request.Include))
                throw new ArgumentNullException(nameof(request.Include));
            if (request.Include != "all" && request.Include != "new")
                throw new ArgumentException(
                    "'Include' must be either 'all' to include all AutoQuery Services or 'new' to include only missing Services and Types", 
                    nameof(request.Include));
            
            var metadata = req.Resolve<INativeTypesMetadata>();
            var genServices = HostContext.AssertPlugin<AutoQueryFeature>().GenerateCrudServices;

            var dbFactory = req.TryResolve<IDbConnectionFactory>();
            var results = request.NoCache == true 
                ? GetTableSchemas(dbFactory, request.Schema, request.NamedConnection)
                : genServices.GetCachedDbSchema(dbFactory, request.Schema, request.NamedConnection).Tables;

            var appHost = HostContext.AppHost;
            request.BaseUrl ??= HostContext.GetPlugin<NativeTypesFeature>().MetadataTypesConfig.BaseUrl ?? appHost.GetBaseUrl(req);
            if (request.MakePartial == null)
                request.MakePartial = false;
            if (request.MakeVirtual == null)
                request.MakeVirtual = false;
            if (request.InitializeCollections == null)
                request.InitializeCollections = false;

            var typesConfig = metadata.GetConfig(request);
            typesConfig.UsePath = req.PathInfo;
            var metadataTypes = metadata.GetMetadataTypes(req, typesConfig);
            var serviceModelNs = appHost.GetType().Namespace + ".ServiceModel";
            var typesNs = serviceModelNs + ".Types";

            //Put NamedConnections Types in their own Namespace
            if (request.NamedConnection != null)
            {
                serviceModelNs += "." + StringUtils.SnakeCaseToPascalCase(request.NamedConnection);
                typesNs = serviceModelNs;
            }
            
            metadataTypes.Namespaces.Add(serviceModelNs);
            metadataTypes.Namespaces.Add(typesNs);
            metadataTypes.Namespaces = metadataTypes.Namespaces.Distinct().ToList();
            
            var crudVerbs = new Dictionary<string,string> {
                { AutoCrudOperation.Query, HttpMethods.Get },
                { AutoCrudOperation.Create, HttpMethods.Post },
                { AutoCrudOperation.Update, HttpMethods.Put },
                { AutoCrudOperation.Patch, HttpMethods.Patch },
                { AutoCrudOperation.Delete, HttpMethods.Delete },
                { AutoCrudOperation.Save, HttpMethods.Post },
            };
            var allVerbs = crudVerbs.Values.Distinct().ToArray();

            var existingRoutes = new HashSet<Tuple<string,string>>();
            foreach (var op in appHost.Metadata.Operations)
            {
                foreach (var route in op.Routes.Safe())
                {
                    var routeVerbs = route.Verbs.IsEmpty() ? allVerbs : route.Verbs;
                    foreach (var verb in routeVerbs)
                    {
                        existingRoutes.Add(new Tuple<string, string>(route.Path, verb));
                    }
                }
            }

            var typesToGenerateMap = new Dictionary<string, TableSchema>();
            foreach (var result in results)
            {
                var keysCount = result.Columns.Count(x => x.IsKey);
                if (keysCount != 1) // Only support tables with 1 PK
                    continue;
                
                typesToGenerateMap[result.Name] = result;
            }
            
            var includeCrudServices = request.IncludeCrudOperations ?? genServices.IncludeCrudOperations;
            var includeCrudInterfaces = AutoCrudOperation.CrudInterfaceMetadataNames(includeCrudServices);
            
            var existingTypes = new HashSet<string>();
            var operations = new List<MetadataOperationType>();
            var types = new List<MetadataType>();
            
            var appDlls = new List<Assembly>();
            var exactTypesLookup = new Dictionary<Tuple<string,string>, MetadataType>();
            foreach (var op in metadataTypes.Operations)
            {
                exactTypesLookup[key(op.Request)] = op.Request;
                if (op.Response != null)
                    exactTypesLookup[key(op.Response)] = op.Response;
                
                if (op.Request.Type != null)
                    appDlls.Add(op.Request.Type.Assembly);
                if (op.Response?.Type != null)
                    appDlls.Add(op.Response.Type.Assembly);
            }
            foreach (var metaType in metadataTypes.Types)
            {
                exactTypesLookup[key(metaType)] = metaType;
                if (metaType.Type != null)
                    appDlls.Add(metaType.Type.Assembly);
            }
            
            // Also don't include Types in App's Implementation Assemblies
            appHost.ServiceAssemblies.Each(x => appDlls.Add(x));
            var existingAppTypes = new HashSet<string>();
            var existingExactAppTypes = new HashSet<Tuple<string,string>>();
            foreach (var appType in appDlls.SelectMany(x => x.GetTypes()))
            {
                existingAppTypes.Add(appType.Name);
                existingExactAppTypes.Add(keyNs(appType.Namespace, appType.Name));
            }

            // Re-use existing Types with same name for default DB Connection
            if (request.NamedConnection == null)
            {
                foreach (var op in metadataTypes.Operations)
                {
                    if (op.Request.Implements?.Any(x => includeCrudInterfaces.Contains(x.Name)) == true)
                    {
                        operations.Add(op);
                    }
                    existingTypes.Add(op.Request.Name);
                    if (op.Response != null)
                        existingTypes.Add(op.Response.Name);
                }
                foreach (var metaType in metadataTypes.Types)
                {
                    if (typesToGenerateMap.ContainsKey(metaType.Name))
                    {
                        types.Add(metaType);
                    }
                    existingTypes.Add(metaType.Name);
                }
            }
            else
            {
                // Only re-use existing Types with exact Namespace + Name for NamedConnection Types
                foreach (var op in metadataTypes.Operations)
                {
                    if (op.Request.Implements?.Any(x => includeCrudInterfaces.Contains(x.Name)) == true &&
                        exactTypesLookup.ContainsKey(keyNs(serviceModelNs, op.Request.Name)))
                    {
                        operations.Add(op);
                    }
                    
                    if (exactTypesLookup.ContainsKey(key(op.Request)))
                        existingTypes.Add(op.Request.Name);
                    if (op.Response != null && exactTypesLookup.ContainsKey(key(op.Request)))
                        existingTypes.Add(op.Response.Name);
                }
                foreach (var metaType in metadataTypes.Types)
                {
                    if (typesToGenerateMap.ContainsKey(metaType.Name) && 
                        exactTypesLookup.ContainsKey(keyNs(serviceModelNs, metaType.Name)))
                    {
                        types.Add(metaType);
                    }
                    if (exactTypesLookup.ContainsKey(keyNs(typesNs, metaType.Name)))
                        existingTypes.Add(metaType.Name);
                }
            }

            var crudMetadataTypes = new MetadataTypes {
                Config = metadataTypes.Config,
                Namespaces = metadataTypes.Namespaces,
                Operations = operations,
                Types = types,
            };
            if (request.Include == "new")
            {
                crudMetadataTypes.Operations = new List<MetadataOperationType>();
                crudMetadataTypes.Types = new List<MetadataType>();
            }

            using var db = request.NamedConnection == null
                ? dbFactory.OpenDbConnection()
                : dbFactory.OpenDbConnection(request.NamedConnection);
            var dialect = db.GetDialectProvider();

            List<MetadataPropertyType> toMetaProps(IEnumerable<ColumnSchema> columns, bool isModel=false)
            {
                var to = new List<MetadataPropertyType>();
                foreach (var column in columns)
                {
                    var dataType = genServices.ResolveColumnType(column, dialect);
                    if (dataType == null)
                        continue;
                    
                    var isKey = column.IsKey || column.IsAutoIncrement;

                    var prop = new MetadataPropertyType {
                        PropertyType = dataType,
                        Name = StringUtils.SnakeCaseToPascalCase(column.ColumnName),
                        Type = dataType.GetMetadataPropertyType(),
                        IsValueType = dataType.IsValueType ? true : (bool?) null,
                        IsSystemType = dataType.IsSystemType() ? true : (bool?) null,
                        IsEnum = dataType.IsEnum ? true : (bool?) null,
                        TypeNamespace = dataType.Namespace,
                    };
                    
                    if (dataType.IsValueType && column.AllowDBNull && !isKey)
                    {
                        prop.Type += "?";
                    }
                    
                    var attrs = new List<MetadataAttribute>();
                    if (isModel)
                    {
                        prop.TypeNamespace = typesNs;
                        if (column.IsKey && column.ColumnName != IdUtils.IdField && !column.IsAutoIncrement)
                            prop.AddAttribute(new PrimaryKeyAttribute());
                        if (column.IsAutoIncrement)
                            prop.AddAttribute(new AutoIncrementAttribute());
                        if (!string.Equals(dialect.NamingStrategy.GetColumnName(prop.Name), column.ColumnName, StringComparison.OrdinalIgnoreCase))
                            prop.AddAttribute(new AliasAttribute(column.ColumnName));
                        if (!dataType.IsValueType && !column.AllowDBNull && !isKey)
                            prop.AddAttribute(new RequiredAttribute());
                    }

                    if (attrs.Count > 0)
                        prop.Attributes = attrs;
                    
                    to.Add(prop);
                }
                return to;
            }

            bool containsType(string ns, string requestType) => request.NamedConnection == null
                ? existingTypes.Contains(requestType) || existingAppTypes.Contains(requestType)
                : exactTypesLookup.ContainsKey(keyNs(ns, requestType)) || existingExactAppTypes.Contains(keyNs(ns, requestType));

            void addToExistingTypes(string ns, MetadataType type)
            {
                existingTypes.Add(type.Name);
                exactTypesLookup[keyNs(ns, type.Name)] = type;
            }

            foreach (var entry in typesToGenerateMap)
            {
                var typeName = StringUtils.SnakeCaseToPascalCase(entry.Key);
                var tableSchema = entry.Value;
                if (includeCrudServices != null)
                {
                    var pkField = tableSchema.Columns.First(x => x.IsKey);
                    var id = StringUtils.SnakeCaseToPascalCase(pkField.ColumnName);
                    foreach (var operation in includeCrudServices)
                    {
                        if (!AutoCrudOperation.IsOperation(operation))
                            continue;

                        var requestType = operation + typeName;
                        if (containsType(serviceModelNs, requestType))
                            continue;

                        var verb = crudVerbs[operation];
                        var plural = Words.Pluralize(typeName).ToLower();
                        var route = verb == "GET" || verb == "POST"
                            ? "/" + plural
                            : "/" + plural + "/{" + id + "}";
                            
                        var op = new MetadataOperationType {
                            Actions = new List<string> { verb },
                            Request = new MetadataType {
                                Routes = new List<MetadataRoute> {
                                },
                                Name = requestType,
                                Namespace = serviceModelNs,
                                Implements = new [] { 
                                    new MetadataTypeName {
                                        Name = "I" + verb[0] + verb.Substring(1).ToLower(), //marker interface 
                                    },
                                },
                            },
                        };
                        
                        if (!existingRoutes.Contains(new Tuple<string, string>(route, verb)))
                            op.Request.Routes.Add(new MetadataRoute { Path = route, Verbs = verb });

                        if (verb == HttpMethods.Get)
                        {
                            op.Request.Inherits = new MetadataTypeName {
                                Namespace = "ServiceStack",
                                Name = "QueryDb`1",
                                GenericArgs = new[] {typeName},
                            };
                            
                            var uniqueRoute = "/" + plural + "/{" + id + "}";
                            if (!existingRoutes.Contains(new Tuple<string, string>(uniqueRoute, verb)))
                            {
                                op.Request.Routes.Add(new MetadataRoute {
                                    Path = uniqueRoute, 
                                    Verbs = verb
                                });
                            }
                        }
                        else
                        {
                            op.Request.Implements = new List<MetadataTypeName>(op.Request.Implements) {
                                new MetadataTypeName {
                                    Name = $"I{operation}Db`1",
                                    GenericArgs = new[] {
                                        typeName,
                                    }
                                },
                            }.ToArray();
                            op.Response = new MetadataType {
                                Name = "IdResponse",
                                Namespace = "ServiceStack",
                            };
                        }

                        var allProps = toMetaProps(tableSchema.Columns);
                        switch (operation)
                        {
                            case AutoCrudOperation.Query:
                                // Only Id Property (use implicit conventions)
                                op.Request.Properties = new List<MetadataPropertyType> {
                                    new MetadataPropertyType {
                                        Name = id,
                                        Type = pkField.DataType.Name + (pkField.DataType.IsValueType ? "?" : ""),
                                        TypeNamespace = pkField.DataType.Namespace, 
                                    }
                                };
                                break;
                            case AutoCrudOperation.Create:
                                // all props - AutoIncrement/AutoId PK
                                var autoId = tableSchema.Columns.FirstOrDefault(x => x.IsAutoIncrement);
                                if (autoId != null)
                                {
                                    op.Request.Properties = toMetaProps(tableSchema.Columns)
                                        .Where(x => !x.Name.EqualsIgnoreCase(autoId.ColumnName)).ToList();
                                }
                                else
                                {
                                    op.Request.Properties = allProps;
                                }
                                break;
                            case AutoCrudOperation.Update:
                                // all props
                                op.Request.Properties = allProps;
                                break;
                            case AutoCrudOperation.Patch:
                                // all props
                                op.Request.Properties = allProps;
                                break;
                            case AutoCrudOperation.Delete:
                                // PK prop
                                var pks = tableSchema.Columns.Where(x => x.IsKey).ToList();
                                op.Request.Properties = toMetaProps(pks);
                                break;
                            case AutoCrudOperation.Save:
                                // all props
                                op.Request.Properties = allProps;
                                break;
                        }
                        
                        crudMetadataTypes.Operations.Add(op);
                        
                        addToExistingTypes(serviceModelNs, op.Request);
                        if (op.Response != null)
                            addToExistingTypes(serviceModelNs, op.Response);
                    }
                }

                if (!containsType(typesNs, typeName))
                {
                    var modelType = new MetadataType {
                        Name = typeName,
                        Namespace = typesNs,
                        Properties = toMetaProps(tableSchema.Columns, isModel:true),
                    };
                    if (request.NamedConnection != null)
                        modelType.AddAttribute(new NamedConnectionAttribute(request.NamedConnection));
                    if (request.Schema != null)
                        modelType.AddAttribute(new SchemaAttribute(request.Schema));
                    if (!string.Equals(dialect.NamingStrategy.GetTableName(typeName), tableSchema.Name, StringComparison.OrdinalIgnoreCase))
                        modelType.AddAttribute(new AliasAttribute(tableSchema.Name));
                    crudMetadataTypes.Types.Add(modelType);

                    addToExistingTypes(typesNs, modelType);
                }
            }

            if (genServices.IncludeService != null)
            {
                crudMetadataTypes.Operations = crudMetadataTypes.Operations.Where(genServices.IncludeService).ToList();
            }
            if (genServices.IncludeType != null)
            {
                crudMetadataTypes.Types = crudMetadataTypes.Types.Where(genServices.IncludeType).ToList();
            }

            if (genServices.ServiceFilter != null)
            {
                foreach (var op in crudMetadataTypes.Operations)
                {
                    genServices.ServiceFilter(op, req);
                }
            }

            if (genServices.TypeFilter != null)
            {
                foreach (var type in crudMetadataTypes.Types)
                {
                    genServices.TypeFilter(type, req);
                }
            }
            
            genServices.MetadataTypesFilter?.Invoke(crudMetadataTypes, typesConfig, req);
            
            return new Tuple<MetadataTypes, MetadataTypesConfig>(crudMetadataTypes, typesConfig);
        }
        
    }
    
    [Restrict(VisibilityTo = RequestAttributes.None)]
    [DefaultRequest(typeof(CrudCodeGenTypes))]
    public class CrudCodeGenTypesService : Service
    {
        [AddHeader(ContentType = MimeTypes.PlainText)]
        public object Any(CrudCodeGenTypes request)
        {
            try
            {
                var genServices = HostContext.AssertPlugin<AutoQueryFeature>().GenerateCrudServices;
                RequestUtils.AssertIsAdminOrDebugMode(base.Request, adminRole: genServices.AccessRole, authSecret: request.AuthSecret);
                
                var src = GenerateCrudServices.GenerateSource(Request, request);
                return src;
            }
            catch (Exception e)
            {
                base.Response.StatusCode = e.ToStatusCode();
                base.Response.StatusDescription = e.GetType().Name;
                return e.ToString();
            }
        }
    }
    
    public class GetMissingTypesToCreate {}

    [Restrict(VisibilityTo = RequestAttributes.None)]
    [DefaultRequest(typeof(CrudTables))]
    public class CrudTablesService : Service
    {
        public object Any(CrudTables request)
        {
            var genServices = HostContext.AssertPlugin<AutoQueryFeature>().GenerateCrudServices;
            RequestUtils.AssertIsAdminOrDebugMode(Request, adminRole: genServices.AccessRole, authSecret: request.AuthSecret);
            
            var dbFactory = TryResolve<IDbConnectionFactory>();
            var results = request.NoCache == true 
                ? GenerateCrudServices.GetTableSchemas(dbFactory, request.Schema, request.NamedConnection)
                : genServices.GetCachedDbSchema(dbFactory, request.Schema, request.NamedConnection).Tables;

            return new AutoCodeSchemaResponse {
                Results = results,
            };
        }
#if DEBUG
        public object Any(GetMissingTypesToCreate request) => ((GenerateCrudServices) HostContext
            .AssertPlugin<AutoQueryFeature>()
            .GenerateCrudServices).GetMissingTypesToCreate(out _);
#endif
    }

}