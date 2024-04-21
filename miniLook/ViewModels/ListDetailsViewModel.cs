﻿using CommunityToolkit.Authentication;
using CommunityToolkit.Graph.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Graph;
using Microsoft.UI.Xaml;
using miniLook.Contracts.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;

namespace miniLook.ViewModels;

public partial class ListDetailsViewModel : ObservableRecipient, INavigationAware
{
    private bool loadedMail = false;

    [ObservableProperty]
    private Message? selected;

    [ObservableProperty]
    private string accountName = string.Empty;

    [ObservableProperty]
    private int numberUnread = 0;

    public ObservableCollection<Message> MailItems { get; private set; } = [];

    public ObservableCollection<Event> Events { get; private set; } = [];

    public DispatcherTimer checkTimer = new();
    private GraphServiceClient? _graphClient;
    private DateTimeOffset lastSync = DateTimeOffset.MinValue;

    public ListDetailsViewModel()
    {
        ProviderManager.Instance.ProviderStateChanged += OnProviderStateChanged;

        MailItems.CollectionChanged += MailItems_CollectionChanged;

        checkTimer.Interval = TimeSpan.FromSeconds(10);
        checkTimer.Tick += CheckTimer_Tick;
        checkTimer.Start();
    }

    private void MailItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NumberUnread = MailItems.Where(MailItems => MailItems.IsRead == false).Count();
    }

    private async void CheckTimer_Tick(object? sender, object e)
    {
        if (_graphClient is null)
            return;

        Debug.WriteLine("Checking for new mail");

        string filter = $"receivedDateTime gt {lastSync:yyyy-MM-ddTHH:mm:ssZ}";

        IMessageDeltaCollectionPage messages = await _graphClient.Me.MailFolders.Inbox.Messages
            .Delta()
            .Request()
            .Filter(filter)
            .GetAsync();

        if (messages.Count == 0)
            return;

        foreach (Message message in messages)
            MailItems.Insert(0, message);

        lastSync = DateTimeOffset.UtcNow;
    }

    public async void OnNavigatedTo(object parameter)
    {
        MailItems.Clear();
        Events.Clear();
        await EstablishGraph();
    }

    [RelayCommand]
    private void GoToOutlook()
    {
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://outlook.live.com/mail/0/"));
    }

    [RelayCommand]
    private void Refresh()
    {
        MailItems.Clear();
        Events.Clear();
        TryToLoadMail();
    }

    private async void TryToLoadMail()
    {
        loadedMail = true;

        if (ProviderManager.Instance.GlobalProvider is not IProvider provider)
            return;

        _graphClient = provider.GetClient();

        User me = await _graphClient.Me.Request().GetAsync();
        AccountName = me.DisplayName;

        IMailFolderMessagesCollectionPage messages = await _graphClient.Me.MailFolders.Inbox.Messages
            .Request()
            .Top(100)
            .GetAsync();

        foreach (Message message in messages)
            MailItems.Add(message);

        // Get the user's mailbox settings to determine
        // their time zone
        User user = await _graphClient.Me.Request()
            .Select(u => new { u.MailboxSettings })
            .GetAsync();

        DateTime startOfWeek = GetUtcStartOfWeekInTimeZone(DateTime.Now, user.MailboxSettings.TimeZone);
        DateTime endOfWeek = startOfWeek.AddDays(2);

        List<QueryOption> queryOptions =
        [
            new QueryOption("startDateTime", startOfWeek.ToString("o")),
            new QueryOption("endDateTime", endOfWeek.ToString("o"))
        ];

        IUserCalendarViewCollectionPage events = await _graphClient.Me.CalendarView.Request(queryOptions)
                .Header("Prefer", $"outlook.timezone=\"{user.MailboxSettings.TimeZone}\"")
                .OrderBy("start/dateTime")
                .Top(3)
                .GetAsync();

        foreach (Event ev in events)
            Events.Add(ev);

        lastSync = DateTimeOffset.UtcNow;
    }

    public void OnNavigatedFrom()
    {
    }


    private static async Task EstablishGraph()
    {
        string clientId = Environment.GetEnvironmentVariable("miniLookId", EnvironmentVariableTarget.User) ?? string.Empty;
        string[] scopes = ["User.Read", "Mail.ReadWrite", "offline_access", "Calendars.Read", "MailboxSettings.Read"];

        ProviderManager.Instance.GlobalProvider = new MsalProvider(clientId, scopes);

        if (ProviderManager.Instance.GlobalProvider is not IProvider provider)
            return;

        bool silentSuccess = await provider.TrySilentSignInAsync();

        if (provider.State == ProviderState.SignedOut && !silentSuccess)
        {
            await provider.SignInAsync();
        }
    }

    private void OnProviderStateChanged(object? sender, ProviderStateChangedEventArgs args)
    {
        if (args.NewState != ProviderState.SignedIn || ProviderManager.Instance.GlobalProvider is not IProvider provider)
            return;

        if (!loadedMail && provider?.State == ProviderState.SignedIn)
            TryToLoadMail();
    }

    private static DateTime GetUtcStartOfWeekInTimeZone(DateTime today, string timeZoneId)
    {
        TimeZoneInfo userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        // Assumes Sunday as first day of week
        int diff = System.DayOfWeek.Sunday - today.DayOfWeek;

        // create date as unspecified kind
        DateTime unspecifiedStart = DateTime.SpecifyKind(today.AddDays(diff), DateTimeKind.Unspecified);

        // convert to UTC
        return TimeZoneInfo.ConvertTimeToUtc(unspecifiedStart, userTimeZone);
    }
}
