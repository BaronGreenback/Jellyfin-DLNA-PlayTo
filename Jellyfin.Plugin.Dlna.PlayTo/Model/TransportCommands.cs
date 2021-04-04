using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.Dlna.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dlna.PlayTo.Model
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransportCommands"/> class.
    /// </summary>
    public class TransportCommands
    {
        private const string CommandBase = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><m:{0} xmlns:m=\"{1}\">{2}</m:{0}></s:Body></s:Envelope>";

        private static readonly XNamespace _svc = "urn:schemas-upnp-org:service-1-0";
        private static readonly XName _serviceStateTable = _svc + "serviceStateTable";

        /// <summary>
        /// Gets the state variables.
        /// </summary>
        public List<StateVariable> StateVariables { get; } = new();

        /// <summary>
        /// Gets the service actions.
        /// </summary>
        public List<ServiceAction> ServiceActions { get; } = new();

        /// <summary>
        /// Creates a transport command.
        /// </summary>
        /// <param name="document">The <see cref="XDocument"/> instance.</param>
        /// <param name="logger">The <see cref="ILogger"/> instance.</param>
        /// <returns>A <see cref="TransportCommands"/>.</returns>
        public static TransportCommands Create(XElement document, ILogger logger)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var command = new TransportCommands();

            var actionList = document.Descendants(_svc + "actionList");
            foreach (var container in actionList.Descendants(_svc! + "action"))
            {
                command.ServiceActions.Add(ServiceActionFromXml(container));
            }

            var stateValues = document.Descendants(_serviceStateTable).FirstOrDefault();
            if (stateValues == null)
            {
                return command;
            }

            foreach (var container in stateValues.Elements(_svc! + "stateVariable"))
            {
                command.StateVariables.Add(FromXml(container, logger));
            }

            return command;
        }

        /// <summary>
        /// Builds a html post command.
        /// </summary>
        /// <param name="action">The <see cref="ServiceAction"/> to use.</param>
        /// <param name="xmlNamespace">Namespace to use.</param>
        /// <returns>A string containing the post data.</returns>
        public string BuildPost(ServiceAction action, string xmlNamespace)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            var stateString = string.Empty;

            foreach (var arg in action.ArgumentList)
            {
                if (arg.Direction == ArgumentDirection.Out)
                {
                    continue;
                }

                if (string.Equals(arg.Name, "InstanceID", StringComparison.Ordinal))
                {
                    stateString += BuildArgumentXml(arg, "0");
                }
                else
                {
                    stateString += BuildArgumentXml(arg, null);
                }
            }

            return string.Format(CultureInfo.InvariantCulture, CommandBase, action.Name, xmlNamespace, stateString);
        }

        /// <summary>
        /// Builds a html post.
        /// </summary>
        /// <param name="action">The <see cref="ServiceAction"/> to use.</param>
        /// <param name="xmlNamespace">Namespace to use.</param>
        /// <param name="value">Value to use as a parameter.</param>
        /// <param name="commandParameter">Value to use for the service parameters.</param>
        /// <returns>A string containing the post data.</returns>
        public string BuildPost(ServiceAction action, string xmlNamespace, object? value, string commandParameter = "")
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            var stateString = string.Empty;

            foreach (var arg in action.ArgumentList)
            {
                if (arg.Direction == ArgumentDirection.Out)
                {
                    continue;
                }

                if (string.Equals(arg.Name, "InstanceID", StringComparison.Ordinal))
                {
                    stateString += BuildArgumentXml(arg, "0");
                }
                else
                {
                    stateString += BuildArgumentXml(arg, value?.ToString(), commandParameter);
                }
            }

            return string.Format(CultureInfo.InvariantCulture, CommandBase, action.Name, xmlNamespace, stateString);
        }

        /// <summary>
        /// Builds a html post.
        /// </summary>
        /// <param name="action">The <see cref="ServiceAction"/> to use.</param>
        /// <param name="xmlNamespace">Namespace to use.</param>
        /// <param name="value">Value to use when parameter isn't included in the <paramref name="dictionary"/>.</param>
        /// <param name="dictionary"><see cref="Dictionary{TKey, TValue}"/> of named parameters.</param>
        /// <returns>A string containing the post data.</returns>
        public string BuildPost(ServiceAction action, string xmlNamespace, object? value, Dictionary<string, string> dictionary)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (dictionary == null)
            {
                throw new ArgumentNullException(nameof(dictionary));
            }

            var stateString = string.Empty;

            foreach (var arg in action.ArgumentList)
            {
                if (string.Equals(arg.Name, "InstanceID", StringComparison.Ordinal))
                {
                    stateString += BuildArgumentXml(arg, "0");
                }
                else if (dictionary.ContainsKey(arg.Name))
                {
                    stateString += BuildArgumentXml(arg, dictionary[arg.Name]);
                }
                else
                {
                    stateString += BuildArgumentXml(arg, value?.ToString());
                }
            }

            return string.Format(CultureInfo.InvariantCulture, CommandBase, action.Name, xmlNamespace, stateString);
        }

        private static ServiceAction ServiceActionFromXml(XElement container)
        {
            var serviceAction = new ServiceAction(container.GetValue(_svc! + "name"));
            var argumentList = serviceAction.ArgumentList;

            foreach (var arg in container.Descendants(_svc! + "argument"))
            {
                argumentList.Add(ArgumentFromXml(arg));
            }

            return serviceAction;
        }

        private static Argument ArgumentFromXml(XElement container)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            if (!Enum.TryParse<ArgumentDirection>(container.GetValue(_svc! + "direction"), true, out var direction))
            {
                direction = ArgumentDirection.In;
            }

            if (!Enum.TryParse<StateVariableType>(container.GetValue(_svc! + "relatedStateVariable"), true, out var variable))
            {
                variable = StateVariableType.Unknown;
            }

            return new Argument(container.GetValue(_svc! + "name"), direction, variable);
        }

        private static StateVariable FromXml(XElement container, ILogger logger)
        {
            var allowedValues = Array.Empty<string>();
            Dictionary<string, string>? allowedValueRange = null;
            var element = container.Descendants(_svc! + "allowedValueList").FirstOrDefault();

            if (element != null)
            {
                var values = element.Descendants(_svc! + "allowedValue");

                allowedValues = values.Select(child => child.Value).ToArray();
            }

            element = container.Descendants(_svc! + "allowedValueRange").FirstOrDefault();

            if (element != null)
            {
                allowedValueRange = new Dictionary<string, string>();
                foreach (var child in element.Descendants())
                {
                    allowedValueRange.Add(child.Name.LocalName, child.Value);
                }
            }

            var svt = container.GetValue(_svc! + "name");
            if (!Enum.TryParse<StateVariableType>(svt, true, out var name))
            {
                logger.LogWarning("Unknown State variable type {name}", svt);
                name = StateVariableType.Unknown;
            }

            var dt = container.GetValue(_svc! + "dataType");
            if (!Enum.TryParse<DataType>("Dt" + dt.Replace(".", "_", StringComparison.Ordinal), true, out var datatype))
            {
                logger.LogWarning("Unknown Data type {name}", dt);
                datatype = DataType.DtUnknown;
            }

            return new StateVariable(name, datatype, false)
            {
                AllowedValues = allowedValues,
                AllowedValueRange = allowedValueRange
            };
        }

        private string BuildArgumentXml(Argument argument, string? value, string commandParameter = "")
        {
            var state = StateVariables.FirstOrDefault(a => a.Name == argument.RelatedStateVariable);

            if (state == null)
            {
                return $"<{argument.Name}>{value}</{argument.Name}>";
            }

            var sendValue = state.AllowedValues?.FirstOrDefault(a => string.Equals(a, commandParameter, StringComparison.OrdinalIgnoreCase)) ??
                                (state.AllowedValues?.Count > 0 ? state.AllowedValues[0] : value);

            var dataType = state.DataType.ToDlnaString();
            return $"<{argument.Name} xmlns:dt=\"urn:schemas-microsoft-com:datatypes\" dt:dt=\"{dataType}\">{sendValue}</{argument.Name}>";
        }
    }
}
