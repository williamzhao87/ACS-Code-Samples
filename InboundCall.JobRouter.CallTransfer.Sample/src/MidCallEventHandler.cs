using Azure.Communication;
using Azure.Communication.CallingServer;
using Azure.Communication.JobRouter;
using JasonShave.Azure.Communication.Service.CallingServer.Sdk.Contracts.V2022_11_1_preview.Events;
using JasonShave.Azure.Communication.Service.EventHandler.CallingServer;
using JasonShave.Azure.Communication.Service.EventHandler.JobRouter;
using JasonShave.Azure.Communication.Service.JobRouter.Sdk.Contracts.V2021_10_20_preview.Events;

namespace InboundCall.JobRouter.CallTransfer.Sample;

public class MidCallEventHandler : BackgroundService
{
    private readonly IRepository<CallConnection> _activeCallsRepository;
    private readonly IRepository<RouterJob> _activeJobsRepository;
    private readonly CallingServerClient _callingServerClient;
    private readonly RouterClient _routerClient;
    private readonly ICallingServerEventSubscriber _interactionEventSubscriber;
    private readonly IJobRouterEventSubscriber _jobRouterEventSubscriber;
    private readonly ILogger<MidCallEventHandler> _logger;

    private const string _transferMri = "[ENTER_YOUR_TRANSFER_MRI_HERE]";

    public MidCallEventHandler(
        IRepository<CallConnection> activeCallsRepository,
        IRepository<RouterJob> activeJobsRepository,
        CallingServerClient callingServerClient,
        RouterClient routerClient,
        ICallingServerEventSubscriber interactionEventSubscriber,
        IJobRouterEventSubscriber jobRouterEventSubscriber,
        ILogger<MidCallEventHandler> logger)
    {
        _activeCallsRepository = activeCallsRepository;
        _activeJobsRepository = activeJobsRepository;
        _callingServerClient = callingServerClient;
        _routerClient = routerClient;
        _interactionEventSubscriber = interactionEventSubscriber;
        _jobRouterEventSubscriber = jobRouterEventSubscriber;
        _logger = logger;
    }

    public override void Dispose() => _interactionEventSubscriber.OnCallConnectionStateChanged -= HandleOnCallConnectionStateChanged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // subscribe to events
        _interactionEventSubscriber.OnCallConnectionStateChanged += HandleOnCallConnectionStateChanged;

        _jobRouterEventSubscriber.OnJobCompleted += HandleJobCompleted;
        _jobRouterEventSubscriber.OnJobClosed += HandleJobClosed;
        _jobRouterEventSubscriber.OnWorkerOfferIssued += HandleOfferIssued;
        _jobRouterEventSubscriber.OnWorkerOfferAccepted += HandleOfferAccepted;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    private async ValueTask HandleJobClosed(RouterJobClosed routerJobClosed, string? contextId)
    {
        _logger.LogInformation($"Job closed: {routerJobClosed.JobId}");

        await _activeJobsRepository.Remove(routerJobClosed.JobId);
    }

    private async ValueTask HandleJobCompleted(RouterJobCompleted routerJobCompleted, string? contextId)
    {
        await _routerClient.CloseJobAsync(routerJobCompleted.JobId, routerJobCompleted.AssignmentId);
        _logger.LogInformation($"Job completed: {routerJobCompleted.JobId}");
    }

    private async ValueTask HandleOnCallConnectionStateChanged(CallConnectionStateChanged callState, string? contextId)
    {
        switch (callState.CallConnectionState.ToLower())
        {
            case "connected":
            {
                RouterJob routerJob = await _routerClient.CreateJobAsync(contextId, "Voice", "Alaska_VIP");
                await _activeJobsRepository.Save(routerJob, contextId);
                break;
            }
            case "disconnected":
            {
                await _activeCallsRepository.Remove(contextId);

                // complete the job since the call has been disconnected
                var existingJob = await _activeJobsRepository.Get(contextId);
                if (existingJob is not null)
                {
                    // job exists in local repo; complete it
                    await _routerClient.CompleteJobAsync(existingJob.Id, existingJob.Assignments.Keys.FirstOrDefault());
                }

                break;
            }
        }

        _logger.LogInformation($"Call state: {callState.CallConnectionState} | Call connection ID: {callState.CallConnectionId} | Context ID: {contextId}");
    }

    private async ValueTask HandleOfferIssued(RouterWorkerOfferIssued offerIssued, string? contextId)
    {
        //check if call is still active
        var callConnection = await _activeCallsRepository.Get(offerIssued.JobId);

        if (callConnection is not null)
        {
            // Frank accepts the job
            await _routerClient.AcceptJobOfferAsync("Frank", offerIssued.OfferId);
            _logger.LogInformation($"Accepting job {offerIssued.JobId}");
        }
        else
        {
            await _routerClient.CancelJobAsync(offerIssued.JobId);
            _logger.LogInformation($"Cancelled stale call {offerIssued.JobId}");
        }
    }

    private async ValueTask HandleOfferAccepted(RouterWorkerOfferAccepted offerAccepted, string? contextId)
    {
        // Transfer active call to Frank
        var callConnection = await _activeCallsRepository.Get(offerAccepted.JobId);
        if (callConnection is not null)
        {
            await callConnection.TransferCallToParticipantAsync(
                new CommunicationUserIdentifier(_transferMri));
        }
    }
}