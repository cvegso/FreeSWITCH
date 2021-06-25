using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using NEventSocket;
using NEventSocket.Channels;
using NEventSocket.FreeSwitch;
using NEventSocket.Util;

/// <summary>
/// It simulates an outbound progressive/predictive call scenario by
/// 
///    1) dialing the customer
///    2) playing a message to the customer after the customer answers the call
///    3) creating a conference
///    4) transferring the customer to the conference
///    5) playing queue music for the customer via the conference
///    6) dialing the agent in the background
///    7) transferring the agent to the conference after the agent answers the call
///    8) letting the agent and the customer to talk to each other
///    9) recording the agent - customer conversation via the conference
/// 
/// </summary>
namespace OutboundCall
{
    class Program
    {
        /// <summary>
        /// The IPv4 address of the FusionPBX/FreeSWITCH instance.
        /// </summary>
        private const string freeSwitchIPv4 = "10.168.3.225";

        /// <summary>
        /// The Event Socket Port on the FusionPBX/FreeSWITCH instance.
        /// </summary>
        private const int freeSwitchEventPort = 8021;

        /// <summary>
        /// The Event Socket Password on the FusionPBX/FreeSWITCH instance.
        /// </summary>
        private const string freeSwitchEventSocketPassword = "ClueCon";

        /// <summary>
        /// A valid SIP extension; being dialed as the customer.
        /// </summary>
        private static readonly string customerUri = $"sofia/external/4448@{freeSwitchIPv4}";

        /// <summary>
        /// A valid SIP extension; being dialed as the agent.
        /// </summary>
        private static readonly string agentUri = $"sofia/external/4449@{freeSwitchIPv4}";

        /// <summary>
        /// The built-in FusionPBX/FreeSWITCH sound file used as welcome message.
        /// </summary>
        private const string welcomeMessageSoundFile = "ivr/8000/ivr-welcome_to_freeswitch.wav";

        /// <summary>
        /// The directory to store recordings.
        /// </summary>
        private const string recordingLocation = "/var/tmp";

        /// <summary>
        /// Client to initiate outbound calls via FusionPBX/FreeSWITCH.
        /// </summary>
        private static InboundSocket callInitiator = null;

        private static string customerChannelUuid = null;
        private static string agentChannelUuid = null;
        
        static void Main(string[] args)
        {
            string logLabel = nameof(Main);

            NEventSocket.Logging.Logger.Configure(new Microsoft.Extensions.Logging.LoggerFactory());

            SetupNEventSocketClient().Wait();

            Task.Run(async () =>
            {
                await RunOutboundScenario();
            });

            Console.WriteLine($"{logLabel} - Press 'q' to quit ...");

            while (Console.ReadKey().KeyChar != 'q') ;

            /*
             * Cleaning up resources.
             */

            TerminateCalls().Wait();
            DestroyNEventSocketClient();
        }

        /// <summary>
        /// It sets up NEventSocket related outbound resources.
        /// </summary>
        /// <returns></returns>
        private static async Task SetupNEventSocketClient()
        {
            string logLabel = nameof(SetupNEventSocketClient);

            callInitiator = await InboundSocket.Connect(freeSwitchIPv4, freeSwitchEventPort, freeSwitchEventSocketPassword);

            callInitiator.ChannelEvents.Subscribe(e =>
            {
                Console.WriteLine($"{logLabel} -  ChannelEvent is received. ChannelCallUUID: {e.ChannelCallUUID}, " +
                    $"EventName: {e.EventName}, ChannelState: {e.ChannelState}, AnswerState: {e.AnswerState}");

                if (e.AnswerState == AnswerState.Answered)
                {
                    /*
                     * These AnswerState events seem to be totally unreliable.
                     */

                    if (customerChannelUuid != null && e.ChannelCallUUID == customerChannelUuid)
                    {
                        Console.WriteLine($"{logLabel} - Customer answered the call");
                    }
                    else if(agentChannelUuid != null && e.ChannelCallUUID == agentChannelUuid)
                    {
                        Console.WriteLine($"{logLabel} - Agent answered the call");
                    }
                }
            });
        }

        /// <summary>
        /// It releases NEventSocket related inbound/outbound resources.
        /// </summary>
        private static void DestroyNEventSocketClient()
        {
            callInitiator.Dispose();
            callInitiator = null;
        }

        /// <summary>
        /// It starts executing the outbound call scenario.
        /// </summary>
        /// <param name="customerChannel"></param>
        /// <returns></returns>
        private static async Task RunOutboundScenario()
        {
            /*
             * Let's dial the customer first.
             */

            customerChannelUuid = await Dial(customerUri);

            if (customerChannelUuid != null)
            {
                /*
                 * Let's play welcome message to the customer.
                 */

                await PlayP2PMessage(customerChannelUuid, welcomeMessageSoundFile);

                /*
                 * Let's escalate customer's call to conference.
                 */

                string conferenceId = await EscalateCallToConference(customerChannelUuid);

                /*
                 * Let's dial the agent in the background.
                 */

                agentChannelUuid = await Dial(agentUri);

                /*
                 * Let's join the agent to the conference.
                 */

                await JoinCallToConference(customerChannelUuid, conferenceId);

                /*
                 * Let's start recording.
                 */

                await StartRecordingOnChannel(customerChannelUuid);
            }
        }

        /// <summary>
        /// It starts playing the specified message to the remote party of the specified call.
        /// </summary>
        /// <param name="channelUuid"></param>
        /// <param name="media"></param>
        /// <returns></returns>
        private static async Task<bool> PlayP2PMessage(string channelUuid, string media)
        {
            string logLabel = nameof(PlayP2PMessage);

            try
            {
                Console.WriteLine($"{logLabel} - Playing media: {media} to channel: {channelUuid}");

                /*
                 * NOTE: awaiting on the Task returned by Play() will block until playing 
                 * the messages finishes. Let's await.
                 * 
                 * NOTE: it seems there is no NEventSocket functionality to monitor the progress 
                 * of a message being played. So the only way here to detect when playing the  
                 * message finished is to await on the Task.
                 */

                PlayResult playResult = await callInitiator.Play(channelUuid, media);

                return playResult.Success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to play message. Reason: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// It escalates the specified call to conference.
        /// </summary>
        /// <param name="channelUuid"></param>
        /// <returns></returns>
        private static async Task<string> EscalateCallToConference(string channelUuid)
        {
            string logLabel = nameof(EscalateCallToConference);

            try
            {
                string conferenceId = $"AdHocConference_{channelUuid}";

                /*
                 * NOTE: awaiting on the Task returned by Execute("conference") will block until  
                 * the conference finishes. Let's not await.
                 */

                Task task = callInitiator.ExecuteApplication(channelUuid, "conference", conferenceId);

                return await Task.FromResult(conferenceId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to escalate call to conference. Reason: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// It dials the specified party/uri.
        /// </summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        private static async Task<string> Dial(string uri)
        {
            string logLabel = nameof(Dial);

            try
            {
                Console.WriteLine($"{logLabel} - Dialing uri: {uri}");

                string channelUuid = Guid.NewGuid().ToString();

                OriginateOptions originateOptions = new OriginateOptions()
                {
                    IgnoreEarlyMedia = false,
                    TimeoutSeconds = 30,
                    UUID = channelUuid
                };

                /*
                 * NOTE: awaiting on the Task returned by Originate() will block only until  
                 * the dialing is initiated. Let's await.
                 * 
                 * NOTE: it seems there is no NEventSocket functionality to monitor the progress 
                 * of an ongoing outbound call. So we do not know when the agent answers the call
                 * here.
                 */

                OriginateResult originateResult = await callInitiator.Originate(uri, options: originateOptions);

                Console.WriteLine($"{logLabel} - Uri: {uri} is dialed. Success: {originateResult.Success}");

                return originateResult.Success ? channelUuid : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to dial the uri. Reason: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// It joins the call having the specified channel Uuid to the 
        /// </summary>
        /// <param name="channelUuid"></param>
        /// <param name="conferenceId"></param>
        /// <returns></returns>
        private static async Task<bool> JoinCallToConference(string channelUuid, string conferenceId)
        {
            string logLabel = nameof(JoinCallToConference);

            try
            {
                Console.WriteLine($"{logLabel} - Joining call with Uuid: {channelUuid} to conference: {conferenceId}");

                /*
                 * NOTE: awaiting on the Task returned by ExecuteApplication("conference") will block until  
                 * the conference finishes. Let's not await.
                 */

                Task task = callInitiator.ExecuteApplication(channelUuid, "conference", conferenceId);

                Console.WriteLine($"{logLabel} - Call with Uuid: {channelUuid} is joined to conference: {conferenceId}");

                return await Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to join call to conference. Reason: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// It terminates the customer/agent calls and the conference.
        /// </summary>
        private static async Task TerminateCalls()
        {
            string logLabel = nameof(TerminateCalls);

            Console.WriteLine($"{logLabel} - Terminating calls and the conference");

            await TerminateChannel(customerChannelUuid);
            await TerminateChannel(agentChannelUuid);

            customerChannelUuid = null;
            agentChannelUuid = null;
        }

        /// <summary>
        /// It terminates the specified channel/call.
        /// </summary>
        /// <param name="channelUuid"></param>
        private static async Task<bool> TerminateChannel(string channelUuid)
        {
            string logLabel = nameof(TerminateChannel);

            try
            {
                if (channelUuid != null)
                {
                    Console.WriteLine($"{logLabel} - Terminating channel: {channelUuid}");

                    await callInitiator.ExecuteApplication(channelUuid, "hangup");

                    return await Task.FromResult(true);
                }
                else
                {
                    Console.WriteLine($"{logLabel} - No need to terminate the channel");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to terminate channel. Reason: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// It starts call recording via the specified channel.
        /// </summary>
        /// <param name="channelUuid"></param>
        /// <returns></returns>
        private static async Task<string> StartRecordingOnChannel(string channelUuid)
        {
            string logLabel = nameof(StartRecordingOnChannel);

            try
            {
                string recordingId = Guid.NewGuid().ToString("N");
                string recordingFilePath = $"{recordingLocation}/{recordingId}.wav";

                Console.WriteLine($"{logLabel} - Starting call recording on the channel: {channelUuid}");

                /*
                 * NOTE: awaiting on the Task returned by ExecuteApplication("record") will block until  
                 * the recording finishes. Let's not await.
                 */

                Task task = callInitiator.ExecuteApplication(channelUuid, "record", recordingFilePath, async: false);

                Console.WriteLine($"{logLabel} - Recording is started with name: {recordingId}, file path: {recordingFilePath}");

                return await Task.FromResult(recordingId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to start recording. Reason: {ex.Message}");
                return null;
            }
        }
    }
}