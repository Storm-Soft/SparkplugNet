﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="SparkplugNode.cs" company="Hämmer Electronics">
// The project is licensed under the MIT license.
// </copyright>
// <summary>
//   A class that handles a Sparkplug node.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace SparkplugNet.Core.Node
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using MQTTnet.Client;
    using MQTTnet.Client.Options;
    using MQTTnet.Formatter;
    using MQTTnet.Protocol;

    using SparkplugNet.Core.Enumerations;
    using SparkplugNet.Core.Extensions;

    using VersionAPayload = VersionA.Payload;
    using VersionBPayload = VersionB.Payload;

    /// <inheritdoc cref="SparkplugBase{T}"/>
    /// <summary>
    /// A class that handles a Sparkplug node.
    /// </summary>
    /// <seealso cref="SparkplugBase{T}"/>
    public class SparkplugNodeBase<T> : SparkplugBase<T> where T : class, new()
    {
        /// <inheritdoc cref="SparkplugBase{T}"/>
        /// <summary>
        /// Initializes a new instance of the <see cref="SparkplugNodeBase{T}"/> class.
        /// </summary>
        /// <param name="knownMetrics">The metric names.</param>
        /// <seealso cref="SparkplugBase{T}"/>
        public SparkplugNodeBase(List<T> knownMetrics) : base(knownMetrics)
        {
        }

        /// <summary>
        /// The callback for the status message received event.
        /// </summary>
        public readonly Action<string>? StatusMessageReceived = null;

        /// <summary>
        /// Gets the device states.
        /// </summary>
        public ConcurrentDictionary<string, MetricState<T>> DeviceStates { get; } = new();

        /// <summary>
        /// Starts the Sparkplug node.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        public async Task Start(SparkplugNodeOptions options)
        {
            // Clear states
            this.DeviceStates.Clear();

            // Load messages
            this.LoadMessages(options);

            // Add handlers
            this.AddDisconnectedHandler(options);
            this.AddMessageReceivedHandler();

            // Connect, subscribe to incoming messages and send a state message
            await this.ConnectInternal(options);
            await this.SubscribeInternal(options);
            await this.PublishInternal(options);
        }

        /// <summary>
        /// Stops the Sparkplug node.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        public async Task Stop()
        {
            await this.Client.DisconnectAsync();
        }

        /// <summary>
        /// Loads the messages used by the the Sparkplug application.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        private void LoadMessages(SparkplugNodeOptions options)
        {
            this.WillMessage = this.MessageGenerator.GetSparkPlugNodeDeathMessage(
                this.NameSpace,
                options.GroupIdentifier,
                options.EdgeNodeIdentifier);

            this.OnlineMessage = this.MessageGenerator.GetSparkPlugNodeBirthMessage(
                this.NameSpace,
                options.GroupIdentifier,
                options.EdgeNodeIdentifier);
        }

        /// <summary>
        /// Adds the disconnected handler and the reconnect functionality to the client.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        private void AddDisconnectedHandler(SparkplugNodeOptions options)
        {
            this.Client.UseDisconnectedHandler(
                async _ =>
                    {
                        // Set all metrics to stale
                        this.UpdateMetricState(SparkplugMetricStatus.Offline);

                        // Invoke disconnected callback
                        this.OnDisconnected?.Invoke();

                        // Wait until the disconnect interval is reached
                        await Task.Delay(options.ReconnectInterval);

                        // Connect, subscribe to incoming messages and send a state message
                        await this.ConnectInternal(options);
                        await this.SubscribeInternal(options);
                        this.UpdateMetricState(SparkplugMetricStatus.Online);
                        await this.PublishInternal(options);
                    });
        }

        /// <summary>
        /// Adds the message received handler to handle incoming messages.
        /// </summary>
        private void AddMessageReceivedHandler()
        {
            this.Client.UseApplicationMessageReceivedHandler(
                e =>
                    {
                        var topic = e.ApplicationMessage.Topic;

                        if (topic.Contains(SparkplugMessageType.NodeCommand.GetDescription()) || topic.Contains(SparkplugMessageType.DeviceCommand.GetDescription()))
                        {
                            switch (this.NameSpace)
                            {
                                case SparkplugNamespace.VersionA:
                                    var payloadVersionA = PayloadHelper.Deserialize<VersionAPayload>(e.ApplicationMessage.Payload);

                                    if (payloadVersionA != null)
                                    {
                                        // Todo: Store metrics for device if metrics are known
                                        this.VersionAPayloadReceived?.Invoke(payloadVersionA);
                                    }

                                    break;

                                case SparkplugNamespace.VersionB:
                                    var payloadVersionB = PayloadHelper.Deserialize<VersionBPayload>(e.ApplicationMessage.Payload);

                                    if (payloadVersionB != null)
                                    {
                                        // Todo: Store metrics for device if metrics are known
                                        this.VersionBPayloadReceived?.Invoke(payloadVersionB);
                                    }

                                    break;
                            }
                        }

                        if (topic.Contains(SparkplugMessageType.StateMessage.GetDescription()))
                        {
                            this.StatusMessageReceived?.Invoke(Encoding.UTF8.GetString(e.ApplicationMessage.Payload));
                        }
                    });
        }

        /// <summary>
        /// Connects the Sparkplug node to the MQTT broker.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        private async Task ConnectInternal(SparkplugNodeOptions options)
        {
            options.CancellationToken ??= CancellationToken.None;

            var builder = new MqttClientOptionsBuilder()
                .WithClientId(options.ClientId)
                .WithCredentials(options.UserName, options.Password)
                .WithCleanSession(false)
                .WithProtocolVersion(MqttProtocolVersion.V311);

            if (options.UseTls)
            {
                builder.WithTls();
            }

            if (options.WebSocketParameters is null)
            {
                builder.WithTcpServer(options.BrokerAddress, options.Port);
            }
            else
            {
                builder.WithWebSocketServer(options.BrokerAddress, options.WebSocketParameters);
            }

            if (options.ProxyOptions != null)
            {
                builder.WithProxy(
                    options.ProxyOptions.Address,
                    options.ProxyOptions.Username,
                    options.ProxyOptions.Password,
                    options.ProxyOptions.Domain,
                    options.ProxyOptions.BypassOnLocal);
            }

            if (this.WillMessage != null)
            {
                builder.WithWillMessage(this.WillMessage);
            }

            this.ClientOptions = builder.Build();

            await this.Client.ConnectAsync(this.ClientOptions, options.CancellationToken.Value);
        }

        /// <summary>
        /// Publishes data to the MQTT broker.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        private async Task PublishInternal(SparkplugNodeOptions options)
        {
            options.CancellationToken ??= CancellationToken.None;
            await this.Client.PublishAsync(this.OnlineMessage, options.CancellationToken.Value);
        }

        /// <summary>
        /// Subscribes the client to the node subscribe topics.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        private async Task SubscribeInternal(SparkplugNodeOptions options)
        {
            var nodeCommandSubscribeTopic = this.TopicGenerator.GetNodeCommandSubscribeTopic(this.NameSpace, options.GroupIdentifier, options.EdgeNodeIdentifier);
            await this.Client.SubscribeAsync(nodeCommandSubscribeTopic, MqttQualityOfServiceLevel.AtLeastOnce);

            var deviceCommandSubscribeTopic = this.TopicGenerator.GetWildcardDeviceCommandSubscribeTopic(this.NameSpace, options.GroupIdentifier, options.EdgeNodeIdentifier);
            await this.Client.SubscribeAsync(deviceCommandSubscribeTopic, MqttQualityOfServiceLevel.AtLeastOnce);

            var stateSubscribeTopic = this.TopicGenerator.GetStateSubscribeTopic(options.ScadaHostIdentifier);
            await this.Client.SubscribeAsync(stateSubscribeTopic, MqttQualityOfServiceLevel.AtLeastOnce);
        }

        /// <summary>
        /// Updates the metric state.
        /// </summary>
        /// <param name="metricState">The metric state.</param>
        private void UpdateMetricState(SparkplugMetricStatus metricState)
        {
            var keys = new List<string>(this.DeviceStates.Keys.ToList());

            foreach (string key in keys)
            {
                this.DeviceStates[key].MetricStatus = metricState;
            }
        }
    }
}