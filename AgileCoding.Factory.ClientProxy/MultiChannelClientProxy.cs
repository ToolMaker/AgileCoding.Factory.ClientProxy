namespace AgileCoding.Factory.ClientProxies
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.ServiceModel;
    using System.ServiceModel.Channels;
    using System.ServiceModel.Configuration;
    using AgileCoding.Extentions.Enums;
    using AgileCoding.Extentions.Exceptions;
    using AgileCoding.Extentions.Generics;
    using AgileCoding.Extentions.Loggers;
    using AgileCoding.Extentions.String;
    using AgileCoding.Library.Enums.Integration;
    using AgileCoding.Library.Enums.SAS;
    using AgileCoding.Library.Interfaces.Logging;
    using AgileCoding.Library.Interfaces.ServiceProxies;
    using AgileCoding.Library.Loggers;
    using AgileCoding.Library.Types;

    public class MultiChannelClientProxy<TProxyClassToBeUsedForNamespace, TMultiChannelProxyInterface>
        where TProxyClassToBeUsedForNamespace : class, IServiceProxy, new()
    {
        private ILogger systemLogger = null;

        private SystemEnvironmentEnum ConnectedEnvironment = SystemEnvironmentEnum.None;

        private const string EnvironmentText = "Environment";

        private Dictionary<ServiceRequestTypeEnum, TMultiChannelProxyInterface> ServiceProxyTypes = null;

        public string ServiceName { get; set; } = "Un Named Client Proxy Service";

        public SystemEnvironmentEnum Environment { get; set; } = SystemEnvironmentEnum.None;

        private TProxyClassToBeUsedForNamespace block = null;

        private Configuration configuraiton = null;

        /// <summary>
        /// If the Config key given (default 'Environment') doesnot exist in the ServiceDLL config (Absa.CustomerCorrespondence.Library.ServiceProxies.ContentRequestService.dll.config)
        /// then the application config is checked. Use the Service config to overrride the application config
        /// NOTE: All Endpoint details should be left in the service config to reduce maintenace on endpoint details. It is not recomended to deviate from this usages
        /// </summary>
        private Configuration ClientProxyConfiguration { get; set; }

        /// <summary>
        /// Creates a Client Proxy Factory the Supports the generation of REST, SOAP11, SOAP12 client proxies. The implementaiton class name should contain any one of the follwong: soap11,soap12 or rest.
        /// </summary>
        /// <param name="clientProxyconfig">This is used to load the DLL configuration. Note: dont use web.config or app.config here.</param>
        /// <param name="serviceName">The Name of the service the proxy is used for. If Rest proxy is define then the Rest endpoint will be stored in AppSettings using this name.</param>
        /// <param name="environmentAppConfigKeyName">This is the App config Key name to loopup in order to know what evironment to point to. If the Environment parm is specified, then it is overridden.</param>
        /// <param name="logger">Logger to use</param>
        /// <param name="environment">Specify environment to override configuration</param>
        public MultiChannelClientProxy(Configuration clientProxyconfig, string serviceName, SystemEnvironmentEnum environment = SystemEnvironmentEnum.None, ILogger logger = null)
        {
            try
            {
                block = new TProxyClassToBeUsedForNamespace();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{nameof(MultiChannelClientProxy<TProxyClassToBeUsedForNamespace, TMultiChannelProxyInterface>)} - private dummy interface class member called '{nameof(block)}' is a SOAP class. This causes configuration errors, were the config file is not found. Either use the Rest proxy or create a dummy proxy as the generic interface.", ex);
            }

            serviceName.ThrowIfIsNullOrEmpty<ArgumentNullException, ArgumentException>(
                $"MultiChannelClientProxy service name parameter is null",
                $"MultiChannelClientProxy service name parameter is Empty");

            if (logger == null)
            {
                this.systemLogger = this.systemLogger.CreateWindowsEventLoggerInstance("Application", $"MultiChannelClientProxy-{ServiceName}");
            }
            else
            {
                this.systemLogger = logger;
            }

            this.systemLogger.WriteVerbose($"Starting {ServiceName} proxy");

            try
            {
                if (environment == SystemEnvironmentEnum.None)
                {
                    if (clientProxyconfig.AppSettings.Settings[EnvironmentText] != null)
                    {
                        environment = this.ConnectedEnvironment = (SystemEnvironmentEnum)Enum.Parse(typeof(SystemEnvironmentEnum), clientProxyconfig.AppSettings.Settings[EnvironmentText].Value);
                    }
                    else
                    {
                        var rootConfig = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                        if (rootConfig.AppSettings.Settings[EnvironmentText] != null)
                        {
                            this.ConnectedEnvironment = (SystemEnvironmentEnum)Enum.Parse(typeof(SystemEnvironmentEnum), rootConfig.AppSettings.Settings[EnvironmentText].Value);
                        }
                        else
                        {
                            throw new ConfigurationErrorsException($"Unable to find AppSetting called '{EnvironmentText}' in either config files {rootConfig.FilePath} and {clientProxyconfig.FilePath}. This Appsetting lets the applicaiton know which environmnet to point to");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new ConfigurationException($"There was a problem determineing the environment for service '{serviceName}'.{System.Environment.NewLine}{ex.ToStringAll()}");
            }
            this.systemLogger.WriteVerbose($"Service '{ServiceName}' Proxy set to '{environment}'");
            try
            {
                this.ConnectedEnvironment = environment;
                environment.ThrowIfEqualTo<SystemEnvironmentEnum, ArgumentException>(SystemEnvironmentEnum.None, $"{this.GetType().Name} Service ({serviceName}) environment cant be set to {SystemEnvironmentEnum.None.ToString()}");
                GenericExtentions.ThrowIfTrue<ArgumentNullException>(clientProxyconfig == null, $"Configuraiton for MultiChannelClientProxy '{serviceName}' cant be null");

                this.ServiceName = serviceName;
                this.systemLogger.WriteVerbose($"{this.GetType().Name} Service ({serviceName}) Pointing to Environment '{this.ConnectedEnvironment}'");

                this.ClientProxyConfiguration = clientProxyconfig;
                this.systemLogger.WriteVerbose($"{this.GetType().Name} Service ({serviceName}) config file used: '{this.ClientProxyConfiguration.FilePath}'");


                if (ServiceProxyTypes == null)
                {
                    this.systemLogger.WriteVerbose($"Initializing Service proxy Types");
                    List<string> namespacesToUse = new List<string>();
                    namespacesToUse.Add(block.GetType().Namespace);

                    List<Type> typesToUse = DynamicLibrary.GetInterfaceTypes<TMultiChannelProxyInterface>(namespacesToUse, true);
                    ServiceProxyTypes = DictionaryOfInstances.CreateDictionaryOfInstancesThatImplmentsENUMInterfaces<ServiceRequestTypeEnum, TMultiChannelProxyInterface>
                        (this.systemLogger,
                        nameof(block.RequestType),
                        typeof(IServiceProxy),
                        typesToUse,
                        (typeDerivedFromInterfaceName) =>
                        {
                            ILogger proxyLogger = this.systemLogger.CreateWindowsEventLoggerInstance("Application", $"MultiChannelClientProxy-{typeDerivedFromInterfaceName}");
                            this.systemLogger.WriteVerbose($"Generating default paramaters for {typeDerivedFromInterfaceName}");
                            object[] parameters = new object[3];
                            parameters[0] = proxyLogger;

                            EndpointAddress endpointAddress = null;
                            Binding binding = null;

                            if (typeDerivedFromInterfaceName.ToLower().Contains("soap11"))
                            {
                                var endpointChannel = ResolveSOAPEndpoint($"{ServiceName}SOAP11{this.ConnectedEnvironment}");

                                endpointChannel.ThrowIfNull<ChannelEndpointElement, ArgumentNullException>($"Was unable find SAOP11 Endpoint configuraiton called '{ServiceName}SOAP11{this.ConnectedEnvironment}' in config file {ClientProxyConfiguration.FilePath}");
                                endpointAddress = new EndpointAddress(endpointChannel.Address.AbsoluteUri);
                                binding = ResolveBinding(endpointChannel.BindingConfiguration);

                            }
                            else if (typeDerivedFromInterfaceName.ToLower().Contains("soap12"))
                            {
                                var endpointChannel = ResolveSOAPEndpoint($"{ServiceName}SOAP12{this.ConnectedEnvironment}");

                                endpointChannel.ThrowIfNull<ChannelEndpointElement, ArgumentNullException>($"Was unable find SAOP12 Endpoint configuraiton called '{ServiceName}SOAP12{this.ConnectedEnvironment}' in config file {ClientProxyConfiguration.FilePath}");
                                endpointAddress = new EndpointAddress(endpointChannel.Address.AbsoluteUri);
                                binding = ResolveBinding(endpointChannel.BindingConfiguration);
                            }
                            else
                            {
                                endpointAddress = ResolveRESTEndpoint($"{this.ConnectedEnvironment}{ServiceName}URL");
                            }

                            this.systemLogger.WriteInformation($"{typeDerivedFromInterfaceName} endpoint set to : {endpointAddress}, using binding '{binding?.Name}'");

                            parameters[1] = endpointAddress;
                            parameters[2] = binding;
                            return parameters;
                        });
                    this.systemLogger.WriteVerbose($"Done Initializing Service proxy Types");
                }
            }
            catch (Exception ex)
            {
                //this.systemLogger.WriteError(ex.ToStringAll());
                //throw new Exception(ex.ToStringAll());
                throw ex;
            }
        }

        /// <summary>
        /// Creates a Serice client from the Proxy DLL Library
        /// </summary>
        /// <typeparam name="T">Client Proxy type</typeparam>
        /// <param name="environmentEndpointConfigurationName"> Endpoint name pointing to specific environment</param>
        /// <returns>Client Proxy</returns>
        public TMultiChannelProxyInterface CreateProxy(ServiceRequestTypeEnum requestType)
        {
            return ServiceProxyTypes[requestType];
        }

        private Binding ResolveBinding(string bindingConfigurationName)
        {
            var serviceModel = ServiceModelSectionGroup.GetSectionGroup(ClientProxyConfiguration);

            serviceModel.ThrowIfNull<ServiceModelSectionGroup, ArgumentNullException>($"Service Model was not configured in cofnig file '{ClientProxyConfiguration.FilePath}'");
            serviceModel.Bindings.ThrowIfNull<BindingsSection, ArgumentNullException>($"Error no Bindings configured. Expecting a SOAP 1.1 (Text) and SOAP 1.2 (MTOM) configuration in file '{ClientProxyConfiguration.FilePath}'");
            serviceModel.Bindings.BindingCollections.ThrowIfNullOrEmpty<BindingCollectionElement, ArgumentNullException>($"BindingCollecitons is null or empty.Expecting a SOAP 1.1 (Text) and SOAP 1.2 (MTOM) configuration in file '{ClientProxyConfiguration.FilePath}'");

            foreach (var bindingCollection in serviceModel.Bindings.BindingCollections)
            {
                foreach (var bindingElement in bindingCollection.ConfiguredBindings)
                {
                    if (bindingElement.Name.Equals(bindingConfigurationName))
                    {
                        var binding = (Binding)Activator.CreateInstance(bindingCollection.BindingType);
                        binding.Name = bindingElement.Name;
                        bindingElement.ApplyConfiguration(binding);
                        return binding;
                    }
                }
            }

            return null;
        }

        private EndpointAddress ResolveRESTEndpoint(string restEndpointName)
        {
            try
            {
                string url = ClientProxyConfiguration.AppSettings.Settings[restEndpointName].Value;
                url.ThrowIfNull<string, ConfigurationErrorsException>($"Was unable find Rest endpoint AppSetting called '{this.ConnectedEnvironment}{ServiceName}URL' in config file {ClientProxyConfiguration.FilePath}");
                return new EndpointAddress(url);
            }
            catch (NullReferenceException nre)
            {
                throw new ConfigurationErrorsException($"Was looking AppSetting called '{restEndpointName}' for RestEndpoint configuraiton HOWEVER it is not found in config file : '{ClientProxyConfiguration.FilePath}'", nre);
            }
        }

        private ChannelEndpointElement ResolveSOAPEndpoint(string endPointName)
        {
            var serviceModel = ServiceModelSectionGroup.GetSectionGroup(ClientProxyConfiguration);

            serviceModel.ThrowIfNull<ServiceModelSectionGroup, ArgumentNullException>($"Service Model was not configured in cofnig file '{ClientProxyConfiguration.FilePath}'");
            serviceModel.Client.ThrowIfNull<ClientSection, ArgumentNullException>($"Error, no client configuration found. Expecting a SOAP 1.1 (Text) and SOAP 1.2 (MTOM) configuration in file '{ClientProxyConfiguration.FilePath}'");
            serviceModel.Client.Endpoints.ThrowIfNull<ChannelEndpointElementCollection, ArgumentNullException>($"Error, client endpoint configuration found.Expecting a SOAP 1.1(Text) and SOAP 1.2(MTOM) configuration in file '{ClientProxyConfiguration.FilePath}'");

            foreach (ChannelEndpointElement channelEndpoint in serviceModel.Client.Endpoints)
            {
                if (channelEndpoint.Name.Equals(endPointName))
                {
                    return channelEndpoint;
                }
            }

            return null;
        }
    }
}
