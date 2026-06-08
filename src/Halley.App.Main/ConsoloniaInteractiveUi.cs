using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Consolonia;
using Consolonia.Themes;

namespace Halley.App.Main;

public sealed class ConsoloniaInteractiveUi : IInteractiveUi
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsErrorRedirected;

    public bool SupportsCallCreateWizard => true;

    public Task<string?> ReadPasswordAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        _ = output;
        var result = ConsoloniaDialogRunner.Run<string?>(
            cancellationToken,
            completion => new PasswordPromptWindow(completion));
        return Task.FromResult(result);
    }

    public Task<string?> ReadLineAsync(
        TextWriter output,
        string prompt,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null,
        CancellationToken cancellationToken = default)
    {
        _ = output;
        _ = prompt;
        _ = suggestions;
        _ = helpText;
        _ = cancellationToken;
        throw new NotSupportedException("The Consolonia UI uses the structured wizard and password dialog rather than line prompts.");
    }

    public Task<string?> ReadMultilineAsync(
        TextWriter output,
        string prompt,
        IReadOnlyList<InteractiveSuggestion>? suggestions = null,
        string? helpText = null,
        CancellationToken cancellationToken = default)
    {
        _ = output;
        _ = prompt;
        _ = suggestions;
        _ = helpText;
        _ = cancellationToken;
        throw new NotSupportedException("The Consolonia UI uses the structured wizard rather than multiline console prompts.");
    }

    public Task<InteractiveCallCreateResult> RunCallCreateWizardAsync(
        InteractiveCallCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = ConsoloniaDialogRunner.Run<InteractiveCallCreateResult>(
            cancellationToken,
            completion => new CallCreateWizardWindow(request, completion));
        return Task.FromResult(result);
    }
}

internal static class ConsoloniaDialogRunner
{
    [ThreadStatic]
    private static Func<Window>? _windowFactory;

    public static TResult Run<TResult>(
        CancellationToken cancellationToken,
        Func<DialogCompletion<TResult>, Window> windowFactory)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new DialogCompletion<TResult>();
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                _windowFactory = () => windowFactory(completion);
                AppBuilder.Configure<ConsoloniaPromptApplication>()
                    .UseConsolonia()
                    .UseAutoDetectedConsole()
                    .LogToException()
                    .StartWithConsoleLifetime([]);
            }
            catch (Exception ex)
            {
                failure = ex;
                completion.TrySetException(ex);
            }
            finally
            {
                _windowFactory = null;
            }
        });

        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            thread.SetApartmentState(ApartmentState.STA);
#pragma warning restore CA1416
        }

        thread.IsBackground = true;
        thread.Start();

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            return completion.Task.GetAwaiter().GetResult();
        }
        finally
        {
            thread.Join();
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    public static Window CreateWindow()
    {
        if (_windowFactory is null)
        {
            throw new InvalidOperationException("No Consolonia dialog window factory has been registered.");
        }

        return _windowFactory();
    }
}

internal sealed class ConsoloniaPromptApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new ModernTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ConsoloniaLifetime lifetimeWithWindow)
        {
            lifetimeWithWindow.ShutdownMode = ShutdownMode.OnMainWindowClose;
            lifetimeWithWindow.MainWindow = ConsoloniaDialogRunner.CreateWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class DialogCompletion<TResult>
{
    private readonly TaskCompletionSource<TResult> _taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TResult> Task => _taskCompletionSource.Task;

    public bool TrySetResult(TResult result) => _taskCompletionSource.TrySetResult(result);

    public bool TrySetCanceled(CancellationToken cancellationToken) => _taskCompletionSource.TrySetCanceled(cancellationToken);

    public bool TrySetException(Exception exception) => _taskCompletionSource.TrySetException(exception);
}

internal static class InteractiveDialogShortcuts
{
    public static bool IsCancelShortcut(Key key, KeyModifiers modifiers) =>
        key == Key.C && modifiers.HasFlag(KeyModifiers.Control);
}

internal abstract class InteractiveDialogWindow<TResult> : Window
{
    private readonly DialogCompletion<TResult> _completion;
    private readonly TResult _cancelResult;

    protected InteractiveDialogWindow(DialogCompletion<TResult> completion, TResult cancelResult)
    {
        _completion = completion;
        _cancelResult = cancelResult;
        Closed += (_, _) => _completion.TrySetResult(_cancelResult);
        AddHandler(InputElement.KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
    }

    protected void Complete(TResult result)
    {
        if (_completion.TrySetResult(result))
        {
            Close();
        }
    }

    protected void Cancel()
    {
        if (_completion.TrySetResult(_cancelResult))
        {
            Close();
        }
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (!InteractiveDialogShortcuts.IsCancelShortcut(e.Key, e.KeyModifiers))
        {
            return;
        }

        e.Handled = true;
        Cancel();
    }
}

internal sealed class PasswordPromptWindow : InteractiveDialogWindow<string?>
{
    public PasswordPromptWindow(DialogCompletion<string?> completion)
        : base(completion, null)
    {
        Title = "Password";
        Width = 72;
        Height = 10;
        CanResize = false;

        var passwordBox = new TextBox
        {
            PasswordChar = '*',
            RevealPassword = false,
            Watermark = "Enter password"
        };

        var errorText = new TextBlock();

        var submitButton = new Button
        {
            Content = "Submit",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        submitButton.Click += (_, _) =>
        {
            var password = string.IsNullOrWhiteSpace(passwordBox.Text) ? null : passwordBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(password))
            {
                errorText.Text = "A password is required.";
                return;
            }

            Complete(password);
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        cancelButton.Click += (_, _) => Cancel();

        Content = new StackPanel
        {
            Margin = new Thickness(1),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = "Password" },
                new TextBlock { Text = "Enter the password for this login request." },
                passwordBox,
                errorText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Spacing = 1,
                    Children =
                    {
                        cancelButton,
                        submitButton
                    }
                }
            }
        };
    }
}

internal sealed class CallCreateWizardWindow : InteractiveDialogWindow<InteractiveCallCreateResult>
{
    private enum WizardStep
    {
        CallSetup,
        Template,
        Manual,
        NotesQuestions,
        Review
    }

    private readonly InteractiveCallCreateRequest _request;
    private readonly List<string> _notes = [];
    private readonly List<InteractiveCallCreateQuestion> _questions = [];
    private readonly TextBlock _stepProgressText;
    private readonly TextBlock _stepTitleText;
    private readonly TextBlock _stepHintText;
    private readonly AutoCompleteBox _organisationBox;
    private readonly TextBlock _organisationInfo;
    private readonly TextBlock _organisationErrorText;
    private readonly RadioButton _templateModeButton;
    private readonly RadioButton _manualModeButton;
    private readonly RadioButton _bothModeButton;
    private readonly RadioButton _phoneMethodButton;
    private readonly RadioButton _webMethodButton;
    private readonly TextBox _phoneNumberBox;
    private readonly AutoCompleteBox _timezoneBox;
    private readonly TextBox _recipientNameBox;
    private readonly AutoCompleteBox _templateBox;
    private readonly TextBlock _templateInfo;
    private readonly AutoCompleteBox _templateVersionBox;
    private readonly TextBox _instructionsBox;
    private readonly TextBox _agendaBox;
    private readonly TextBox _noteInputBox;
    private readonly ListBox _notesListBox;
    private readonly TextBox _questionIdBox;
    private readonly AutoCompleteBox _questionFormatBox;
    private readonly TextBox _questionTextBox;
    private readonly ListBox _questionsListBox;
    private readonly StackPanel _setupPanel;
    private readonly StackPanel _templatePanel;
    private readonly StackPanel _manualPanel;
    private readonly StackPanel _notesQuestionsPanel;
    private readonly StackPanel _reviewPanel;
    private readonly StackPanel _phonePanel;
    private readonly TextBlock _reviewSummaryText;
    private readonly TextBlock _errorText;
    private readonly Button _backButton;
    private readonly Button _nextButton;
    private readonly Button _createButton;
    private IReadOnlyList<InteractiveSuggestion> _currentTemplateSuggestions = [];
    private IReadOnlyList<InteractiveSuggestion> _currentTemplateVersionSuggestions = [];
    private WizardStep _currentStep = WizardStep.CallSetup;
    private CancellationTokenSource? _templateRefreshCancellation;
    private CancellationTokenSource? _templateVersionRefreshCancellation;

    public CallCreateWizardWindow(
        InteractiveCallCreateRequest request,
        DialogCompletion<InteractiveCallCreateResult> completion)
        : base(completion, InteractiveCallCreateResult.CancelledResult())
    {
        _request = request;

        Title = "Create Call";
        Width = 110;
        Height = 40;
        CanResize = true;

        _stepProgressText = new TextBlock();
        _stepTitleText = new TextBlock();
        _stepHintText = new TextBlock();
        _templateModeButton = new RadioButton { Content = "Template" };
        _manualModeButton = new RadioButton { Content = "Manual", GroupName = _templateModeButton.GroupName };
        _bothModeButton = new RadioButton { Content = "Template + Manual", GroupName = _templateModeButton.GroupName };
        _manualModeButton.IsChecked = true;

        _organisationBox = CreateAutoCompleteBox(_request.Organisations, "Search by organisation name");
        _organisationInfo = new TextBlock();
        _organisationErrorText = new TextBlock();
        _phoneMethodButton = new RadioButton { Content = "Phone" };
        _webMethodButton = new RadioButton { Content = "Web", GroupName = _phoneMethodButton.GroupName };
        _phoneMethodButton.IsChecked = true;
        _phoneNumberBox = new TextBox { Watermark = "+61400000000" };
        _recipientNameBox = new TextBox { Watermark = "Test User" };
        _timezoneBox = CreateAutoCompleteBox(_request.Timezones, "Australia/Melbourne");

        _templateBox = CreateAutoCompleteBox([], "Template name or uuid");
        _templateInfo = new TextBlock();
        _templateVersionBox = CreateAutoCompleteBox([], "Specific version id (optional)");

        _instructionsBox = CreateMultilineTextBox("Describe how the agent should behave.");
        _agendaBox = CreateMultilineTextBox("Outline the steps or talking points.");

        _noteInputBox = new TextBox { Watermark = "Add a note and press Add" };
        _notesListBox = new ListBox();
        RefreshNotesList();

        _questionIdBox = new TextBox { Watermark = "1", Width = 8 };
        _questionFormatBox = CreateAutoCompleteBox(
            [
                new("string", "Free text response"),
                new("boolean", "Yes or no response")
            ],
            "string");
        _questionTextBox = new TextBox { Watermark = "Was the resident okay?" };
        _questionsListBox = new ListBox();
        RefreshQuestionsList();

        _phonePanel = CreateFieldPanel("Phone number", _phoneNumberBox, "Required when call method is phone.");
        _reviewSummaryText = new TextBlock();
        _errorText = new TextBlock();

        var addNoteButton = new Button { Content = "Add note" };
        addNoteButton.Click += (_, _) =>
        {
            var note = Normalize(_noteInputBox.Text);
            if (note is null)
            {
                _errorText.Text = "Enter a note before adding it.";
                return;
            }

            _notes.Add(note);
            _noteInputBox.Text = string.Empty;
            _errorText.Text = string.Empty;
            RefreshNotesList();
        };

        var removeNoteButton = new Button { Content = "Remove selected note" };
        removeNoteButton.Click += (_, _) =>
        {
            if (_notesListBox.SelectedItem is string selectedNote && _notes.Remove(selectedNote))
            {
                RefreshNotesList();
            }
        };

        var addQuestionButton = new Button { Content = "Add question" };
        addQuestionButton.Click += (_, _) =>
        {
            if (!TryAddQuestionFromEditor(out var error))
            {
                _errorText.Text = error;
                return;
            }

            _errorText.Text = string.Empty;
        };

        var removeQuestionButton = new Button { Content = "Remove selected question" };
        removeQuestionButton.Click += (_, _) =>
        {
            if (_questionsListBox.SelectedItem is InteractiveCallCreateQuestion selectedQuestion && _questions.Remove(selectedQuestion))
            {
                RefreshQuestionsList();
            }
        };

        _setupPanel = CreateSetupPanel();
        _templatePanel = CreateTemplatePanel();
        _manualPanel = CreateManualPanel();
        _notesQuestionsPanel = CreateNotesQuestionsPanel();
        _reviewPanel = CreateReviewPanel();

        _templateModeButton.IsCheckedChanged += (_, _) => UpdateVisibility();
        _manualModeButton.IsCheckedChanged += (_, _) => UpdateVisibility();
        _bothModeButton.IsCheckedChanged += (_, _) => UpdateVisibility();
        _phoneMethodButton.IsCheckedChanged += (_, _) => UpdateVisibility();
        _webMethodButton.IsCheckedChanged += (_, _) => UpdateVisibility();

        _organisationBox.TextChanged += async (_, _) =>
        {
            UpdateOrganisationInfo();
            await RefreshTemplateSuggestionsAsync();
        };

        _templateBox.TextChanged += async (_, _) =>
        {
            UpdateTemplateInfo();
            await RefreshTemplateVersionSuggestionsAsync();
        };

        _backButton = new Button { Content = "Back" };
        _backButton.Click += (_, _) => MoveBack();

        _nextButton = new Button { Content = "Next" };
        _nextButton.Click += (_, _) => MoveNext();

        _createButton = new Button { Content = "Create call" };
        _createButton.Click += (_, _) =>
        {
            if (!TryBuildResult(out var result, out var error))
            {
                _errorText.Text = error;
                return;
            }

            _errorText.Text = string.Empty;
            Complete(result!);
        };

        var cancelButton = new Button { Content = "Cancel" };
        cancelButton.Click += (_, _) => Cancel();

        Content = new StackPanel
        {
            Margin = new Thickness(1),
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = "Call create wizard" },
                _stepProgressText,
                _stepTitleText,
                _stepHintText,
                new ScrollViewer
                {
                    Height = 26,
                    Content = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            _setupPanel,
                            _templatePanel,
                            _manualPanel,
                            _notesQuestionsPanel,
                            _reviewPanel
                        }
                    }
                },
                _errorText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 1,
                    Children = { cancelButton, _backButton, _nextButton, _createButton }
                }
            }
        };

        UpdateOrganisationInfo();
        UpdateTemplateInfo();
        UpdateWizardStepUi();

        StackPanel CreateNotesEditorPanel() =>
            CreateFieldPanel(
                "Notes",
                new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 1,
                            Children = { _noteInputBox, addNoteButton }
                        },
                        _notesListBox,
                        removeNoteButton
                    }
                },
                "Optional context for the call request.");

        StackPanel CreateQuestionsEditorPanel() =>
            CreateFieldPanel(
                "Result questions",
                new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 1,
                            Children = { _questionIdBox, _questionFormatBox, _questionTextBox, addQuestionButton }
                        },
                        _questionsListBox,
                        removeQuestionButton
                    }
                },
                "Optional structured questions to collect from the call result.");

        StackPanel CreateNotesQuestionsPanel() =>
            new()
            {
                Spacing = 1,
                Children =
                {
                    CreateNotesEditorPanel(),
                    CreateQuestionsEditorPanel()
                }
            };
    }

    private StackPanel CreateSetupPanel() =>
        new()
        {
            Spacing = 1,
            Children =
            {
                CreateFieldPanel(
                    "Call mode",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 1,
                        Children = { _templateModeButton, _manualModeButton, _bothModeButton }
                    },
                    "Choose whether to use a template, manual content, or both."),
                CreateFieldPanel("Organisation", _organisationBox, "Select or type an organisation name. You can still paste an organisation id if needed."),
                _organisationInfo,
                _organisationErrorText,
                CreateFieldPanel(
                    "Call method",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 1,
                        Children = { _phoneMethodButton, _webMethodButton }
                    },
                    "Choose how the call should be delivered."),
                _phonePanel,
                CreateFieldPanel("Recipient name", _recipientNameBox, "Who should receive the call."),
                CreateFieldPanel("Recipient timezone", _timezoneBox, "Use an IANA timezone such as Australia/Melbourne.")
            }
        };

    private StackPanel CreateTemplatePanel() =>
        new()
        {
            Spacing = 1,
            Children =
            {
                CreateFieldPanel("Template", _templateBox, "Start typing to filter visible templates for the selected organisation."),
                _templateInfo,
                CreateFieldPanel("Template version", _templateVersionBox, "Optional specific version id. Leave blank to use the latest visible version.")
            }
        };

    private StackPanel CreateManualPanel() =>
        new()
        {
            Spacing = 1,
            Children =
            {
                CreateFieldPanel("Instructions", _instructionsBox, "Describe how the agent should behave during the call."),
                CreateFieldPanel("Agenda", _agendaBox, "List the steps, prompts, or outcomes the call should cover.")
            }
        };

    private StackPanel CreateReviewPanel() =>
        new()
        {
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = "Check the final request before creating the call." },
                _reviewSummaryText
            }
        };

    private static StackPanel CreateFieldPanel(string label, Control control, string? helpText = null)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(control);
        if (!string.IsNullOrWhiteSpace(helpText))
        {
            panel.Children.Add(new TextBlock { Text = helpText });
        }

        return panel;
    }

    private static AutoCompleteBox CreateAutoCompleteBox(
        IReadOnlyList<InteractiveSuggestion> suggestions,
        string watermark)
    {
        var box = new AutoCompleteBox
        {
            ItemsSource = suggestions.Select(static suggestion => suggestion.Value).ToArray(),
            Watermark = watermark,
            MinimumPrefixLength = 0,
            IsTextCompletionEnabled = true,
            MaxDropDownHeight = 10,
            ItemFilter = static (search, item) =>
            {
                if (item is not string value)
                {
                    return false;
                }

                return string.IsNullOrWhiteSpace(search)
                    || value.Contains(search, StringComparison.OrdinalIgnoreCase);
            },
            TextSelector = static (_, item) => item?.ToString() ?? string.Empty
        };

        return box;
    }

    private static TextBox CreateMultilineTextBox(string watermark) =>
        new()
        {
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Watermark = watermark,
            MinHeight = 4
        };

    private void UpdateVisibility()
    {
        _phonePanel.IsVisible = IsPhoneModeSelected();
        UpdateWizardStepUi();
    }

    private void UpdateOrganisationInfo()
    {
        var suggestion = FindSuggestion(_request.Organisations, _organisationBox.Text);
        _organisationInfo.Text = suggestion?.Description ?? string.Empty;
        _organisationErrorText.Text = SelectedOrganisationLacksHotlineLicense(suggestion)
            ? "The selected organisation does not have an active Hotline license."
            : string.Empty;
    }

    private void UpdateTemplateInfo()
    {
        var suggestion = FindSuggestion(_currentTemplateSuggestions, _templateBox.Text);
        _templateInfo.Text = suggestion?.Description ?? string.Empty;
    }

    private void UpdateWizardStepUi()
    {
        var visibleSteps = GetVisibleSteps();
        if (!visibleSteps.Contains(_currentStep))
        {
            _currentStep = visibleSteps.First();
        }

        _setupPanel.IsVisible = _currentStep == WizardStep.CallSetup;
        _templatePanel.IsVisible = _currentStep == WizardStep.Template;
        _manualPanel.IsVisible = _currentStep == WizardStep.Manual;
        _notesQuestionsPanel.IsVisible = _currentStep == WizardStep.NotesQuestions;
        _reviewPanel.IsVisible = _currentStep == WizardStep.Review;

        var currentIndex = visibleSteps.IndexOf(_currentStep);
        _stepProgressText.Text = $"Step {currentIndex + 1} of {visibleSteps.Count}";

        (_stepTitleText.Text, _stepHintText.Text) = _currentStep switch
        {
            WizardStep.CallSetup => ("Call setup", "Choose the call mode, organisation, delivery method, and recipient details."),
            WizardStep.Template => ("Template", "Choose the template and optional version."),
            WizardStep.Manual => ("Manual content", "Provide the instructions and agenda for the call."),
            WizardStep.NotesQuestions => ("Notes and questions", "Add optional notes and structured result questions."),
            WizardStep.Review => ("Review and create", "Confirm the request details before creating the call."),
            _ => (string.Empty, string.Empty)
        };

        _backButton.IsEnabled = currentIndex > 0;
        _nextButton.IsVisible = currentIndex < visibleSteps.Count - 1;
        _createButton.IsVisible = currentIndex == visibleSteps.Count - 1;

        if (_currentStep == WizardStep.Review)
        {
            UpdateReviewSummary();
        }
    }

    private List<WizardStep> GetVisibleSteps()
    {
        var steps = new List<WizardStep> { WizardStep.CallSetup };
        if (IsTemplateModeSelected())
        {
            steps.Add(WizardStep.Template);
        }

        if (IsManualModeSelected())
        {
            steps.Add(WizardStep.Manual);
        }

        steps.Add(WizardStep.NotesQuestions);
        steps.Add(WizardStep.Review);
        return steps;
    }

    private void MoveBack()
    {
        _errorText.Text = string.Empty;
        var visibleSteps = GetVisibleSteps();
        var currentIndex = visibleSteps.IndexOf(_currentStep);
        if (currentIndex <= 0)
        {
            return;
        }

        _currentStep = visibleSteps[currentIndex - 1];
        UpdateWizardStepUi();
    }

    private void MoveNext()
    {
        if (!TryValidateCurrentStep(out var error))
        {
            _errorText.Text = error;
            return;
        }

        _errorText.Text = string.Empty;
        var visibleSteps = GetVisibleSteps();
        var currentIndex = visibleSteps.IndexOf(_currentStep);
        if (currentIndex >= visibleSteps.Count - 1)
        {
            return;
        }

        _currentStep = visibleSteps[currentIndex + 1];
        UpdateWizardStepUi();
    }

    private bool TryValidateCurrentStep(out string error)
    {
        error = string.Empty;

        switch (_currentStep)
        {
            case WizardStep.CallSetup:
                if (Normalize(_organisationBox.Text) is null)
                {
                    error = "An organisation is required.";
                    return false;
                }

                var matchingOrganisation = FindSuggestion(_request.Organisations, _organisationBox.Text);
                if (SelectedOrganisationLacksHotlineLicense(matchingOrganisation))
                {
                    error = "The selected organisation does not have an active Hotline license.";
                    return false;
                }

                if (Normalize(_recipientNameBox.Text) is null)
                {
                    error = "A recipient name is required.";
                    return false;
                }

                if (Normalize(_timezoneBox.Text) is null)
                {
                    error = "A recipient timezone is required.";
                    return false;
                }

                if (IsPhoneModeSelected() && Normalize(_phoneNumberBox.Text) is null)
                {
                    error = "A phone number is required when call method is phone.";
                    return false;
                }

                return true;

            case WizardStep.Template:
                if (Normalize(_templateBox.Text) is null)
                {
                    error = "A template is required for template-based calls.";
                    return false;
                }

                var templateIdText = Normalize(_templateVersionBox.Text);
                if (templateIdText is not null && (!int.TryParse(templateIdText, out var parsedTemplateId) || parsedTemplateId <= 0))
                {
                    error = "Template version must be a positive integer.";
                    return false;
                }

                return true;

            case WizardStep.NotesQuestions:
                return TryFlushPendingDrafts(out error);

            default:
                return true;
        }
    }

    private bool TryFlushPendingDrafts(out string error)
    {
        error = string.Empty;

        if (Normalize(_noteInputBox.Text) is { } pendingNote)
        {
            _notes.Add(pendingNote);
            _noteInputBox.Text = string.Empty;
            RefreshNotesList();
        }

        if (Normalize(_questionIdBox.Text) is not null
            || Normalize(_questionFormatBox.Text) is not null
            || Normalize(_questionTextBox.Text) is not null)
        {
            return TryAddQuestionFromEditor(out error);
        }

        return true;
    }

    private void UpdateReviewSummary()
    {
        var lines = new List<string>
        {
            $"Organisation: {Normalize(_organisationBox.Text) ?? "(missing)"}",
            $"Call mode: {GetSelectedCallMode()}",
            $"Call method: {(IsPhoneModeSelected() ? "phone" : "web")}",
            $"Recipient: {Normalize(_recipientNameBox.Text) ?? "(missing)"}",
            $"Timezone: {Normalize(_timezoneBox.Text) ?? "(missing)"}"
        };

        if (IsPhoneModeSelected())
        {
            lines.Add($"Phone number: {Normalize(_phoneNumberBox.Text) ?? "(missing)"}");
        }

        if (IsTemplateModeSelected())
        {
            lines.Add($"Template: {Normalize(_templateBox.Text) ?? "(missing)"}");
            lines.Add($"Template version: {Normalize(_templateVersionBox.Text) ?? "(latest visible version)"}");
        }

        if (IsManualModeSelected())
        {
            lines.Add($"Instructions: {(Normalize(_instructionsBox.Text) is null ? "(none)" : "provided")}");
            lines.Add($"Agenda: {(Normalize(_agendaBox.Text) is null ? "(none)" : "provided")}");
        }

        lines.Add($"Notes: {_notes.Count}");
        lines.Add($"Questions: {_questions.Count}");
        _reviewSummaryText.Text = string.Join(Environment.NewLine, lines);
    }

    private async Task RefreshTemplateSuggestionsAsync()
    {
        _templateRefreshCancellation?.Cancel();
        _templateRefreshCancellation = new CancellationTokenSource();
        var cancellationToken = _templateRefreshCancellation.Token;

        try
        {
            await Task.Delay(150, cancellationToken);
            if (!IsTemplateModeSelected())
            {
                _currentTemplateSuggestions = [];
                _currentTemplateVersionSuggestions = [];
                _templateBox.ItemsSource = Array.Empty<string>();
                _templateVersionBox.ItemsSource = Array.Empty<string>();
                return;
            }

            var organisationReference = Normalize(_organisationBox.Text);
            if (organisationReference is null)
            {
                _currentTemplateSuggestions = [];
                _currentTemplateVersionSuggestions = [];
                _templateBox.ItemsSource = Array.Empty<string>();
                _templateVersionBox.ItemsSource = Array.Empty<string>();
                return;
            }

            var suggestions = await _request.LoadTemplateSuggestionsAsync(organisationReference, cancellationToken);
            _currentTemplateSuggestions = suggestions;
            _templateBox.ItemsSource = suggestions.Select(static suggestion => suggestion.Value).ToArray();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshTemplateVersionSuggestionsAsync()
    {
        _templateVersionRefreshCancellation?.Cancel();
        _templateVersionRefreshCancellation = new CancellationTokenSource();
        var cancellationToken = _templateVersionRefreshCancellation.Token;

        try
        {
            await Task.Delay(150, cancellationToken);
            if (!IsTemplateModeSelected())
            {
                _currentTemplateVersionSuggestions = [];
                _templateVersionBox.ItemsSource = Array.Empty<string>();
                return;
            }

            var organisationReference = Normalize(_organisationBox.Text);
            var templateReference = Normalize(_templateBox.Text);
            if (organisationReference is null || templateReference is null)
            {
                _currentTemplateVersionSuggestions = [];
                _templateVersionBox.ItemsSource = Array.Empty<string>();
                return;
            }

            var suggestions = await _request.LoadTemplateVersionSuggestionsAsync(organisationReference, templateReference, cancellationToken);
            _currentTemplateVersionSuggestions = suggestions;
            _templateVersionBox.ItemsSource = suggestions.Select(static suggestion => suggestion.Value).ToArray();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshNotesList() => _notesListBox.ItemsSource = _notes.ToArray();

    private void RefreshQuestionsList() => _questionsListBox.ItemsSource = _questions.ToArray();

    private bool TryAddQuestionFromEditor(out string error)
    {
        error = string.Empty;

        var idText = Normalize(_questionIdBox.Text);
        var format = Normalize(_questionFormatBox.Text);
        var text = Normalize(_questionTextBox.Text);

        if (idText is null && format is null && text is null)
        {
            error = "Enter a question before adding it.";
            return false;
        }

        if (!int.TryParse(idText, out var id) || id <= 0)
        {
            error = "Question ids must be positive integers.";
            return false;
        }

        if (format is null || format is not "string" and not "boolean")
        {
            error = "Question format must be `string` or `boolean`.";
            return false;
        }

        if (text is null)
        {
            error = "Question text is required.";
            return false;
        }

        _questions.Add(new InteractiveCallCreateQuestion(id, text, format));
        _questionIdBox.Text = string.Empty;
        _questionFormatBox.Text = string.Empty;
        _questionTextBox.Text = string.Empty;
        RefreshQuestionsList();
        return true;
    }

    private bool TryBuildResult(out InteractiveCallCreateResult? result, out string error)
    {
        result = null;
        error = string.Empty;

        if (!TryFlushPendingDrafts(out error))
        {
            return false;
        }

        var templateIdText = Normalize(_templateVersionBox.Text);
        int? templateId = null;
        if (templateIdText is not null)
        {
            if (!int.TryParse(templateIdText, out var parsedTemplateId) || parsedTemplateId <= 0)
            {
                error = "Template version must be a positive integer.";
                return false;
            }

            templateId = parsedTemplateId;
        }

        var organizationReference = Normalize(_organisationBox.Text);
        if (organizationReference is null)
        {
            error = "An organisation is required.";
            return false;
        }

        var matchingOrganisation = FindSuggestion(_request.Organisations, organizationReference);
        if (SelectedOrganisationLacksHotlineLicense(matchingOrganisation))
        {
            error = "The selected organisation does not have an active Hotline license.";
            return false;
        }

        result = new InteractiveCallCreateResult(
            false,
            organizationReference,
            IsPhoneModeSelected() ? "phone" : "web",
            IsPhoneModeSelected() ? Normalize(_phoneNumberBox.Text) : null,
            Normalize(_recipientNameBox.Text),
            Normalize(_timezoneBox.Text),
            IsTemplateModeSelected() ? Normalize(_templateBox.Text) : null,
            IsTemplateModeSelected() ? templateId : null,
            IsManualModeSelected() ? Normalize(_instructionsBox.Text) : null,
            IsManualModeSelected() ? Normalize(_agendaBox.Text) : null,
            _notes.ToArray(),
            _questions.ToArray());
        return true;
    }

    private bool IsTemplateModeSelected() =>
        _templateModeButton.IsChecked == true || _bothModeButton.IsChecked == true;

    private bool IsManualModeSelected() =>
        _manualModeButton.IsChecked == true || _bothModeButton.IsChecked == true;

    private bool IsPhoneModeSelected() => _phoneMethodButton.IsChecked == true;

    private string GetSelectedCallMode() =>
        _templateModeButton.IsChecked == true ? "template" :
        _bothModeButton.IsChecked == true ? "template+manual" :
        "manual";

    private static InteractiveSuggestion? FindSuggestion(IEnumerable<InteractiveSuggestion> suggestions, string? value)
    {
        var normalized = Normalize(value);
        return normalized is null
            ? null
            : suggestions.FirstOrDefault(suggestion => string.Equals(suggestion.Value, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SelectedOrganisationLacksHotlineLicense(InteractiveSuggestion? suggestion) =>
        string.Equals(suggestion?.Description, "No Hotline license", StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
