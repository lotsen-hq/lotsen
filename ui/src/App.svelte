<script lang="ts">
   import { onMount } from 'svelte';
   import DiffHeading from './components/DiffHeading.svelte';
   import DiffOptions from './components/DiffOptions.svelte';
   import DiffView from './components/DiffView.svelte';
   import FileHeading from './components/FileHeading.svelte';
   import FilesPanel from './components/FilesPanel.svelte';
   import FileView from './components/FileView.svelte';
   import IterationCheck from './components/IterationCheck.svelte';
   import IterationNotice from './components/IterationNotice.svelte';
   import IterationUpdate from './components/IterationUpdate.svelte';
   import PlanPanel from './components/PlanPanel.svelte';
   import PassageNavigation from './components/PassageNavigation.svelte';
   import StopNavigation from './components/StopNavigation.svelte';
   import StopFiles from './components/StopFiles.svelte';
   import StopPanel from './components/StopPanel.svelte';
   import SummaryAction from './components/SummaryAction.svelte';
   import SummaryPage from './components/SummaryPage.svelte';
   import TooltipScope from './components/TooltipScope.svelte';
   import TopBar from './components/TopBar.svelte';
   import TourPicker from './components/TourPicker.svelte';
   import ViewModeSwitch from './components/ViewModeSwitch.svelte';
   import Workspace from './components/Workspace.svelte';
   import ThreadActionsScope from './components/ThreadActionsScope.svelte';
   import ThreadsPanel from './components/ThreadsPanel.svelte';
   import type { LotsenApp } from './lib/app.svelte';
   import { ExplanationView } from './lib/explanationView.svelte';
   import type { PickedAnchor } from './lib/viewState';
   import { versionOf } from './lib/stops';
   import type { AnswerWrites, CheckAnswers } from './lib/answers';
   import { disabledReason, threadsOf } from './lib/controls';
   import { attachmentOf } from './lib/labels';
   import { LinkHover } from './lib/linkHover';
   import { providePaneSplit } from './lib/paneSplit.svelte';
   import { showsNoEditor } from './lib/placement';
   import { changedFilePaths } from './lib/pin';
   import { changedFileAnchor } from './lib/selectionAsk';
   import { fileThreads } from './lib/threadView';
   import { followUpKinds, type StopActionKind } from './lib/tourActions';
   import { generationStatus } from './lib/generationStatus';
   import type { SummaryAnswer, ThreadAnchor } from './lib/types';
   import { Settings } from './lib/settings.svelte';
   import { WorkspaceLayout } from './lib/workspaceLayout.svelte';

   let { app }: { app: LotsenApp } = $props();

   const layout = new WorkspaceLayout();
   const settings = new Settings(window);
   providePaneSplit();
   const hover = new LinkHover((reference) => app.hoverReference(reference));

   /** Hands what the reviewer picked to the Threads panel, which shows. */
   function pick(anchor: PickedAnchor, quote: string | null): void {
      layout.reveal('threads');
      app.pickAnchor(anchor, quote);
   }

   /** Opens a thread from its marker in the Threads panel, which shows. */
   function openThread(threadId: string): void {
      layout.reveal('threads');
      app.openThread(threadId);
   }

   /** Opens an answer's check from the summary page, with the Stop panel showing. */
   function openCheck(answer: SummaryAnswer): void {
      layout.reveal('stop');
      app.openCheck(answer.stopId, answer.checkId, answer.outdatedOn);
   }

   /** Opens where a thread was asked, with the Stop panel showing for a stop or text in one. */
   function openAnchor(anchor: ThreadAnchor): void {
      if (anchor.kind === 'stop' || anchor.kind === 'stopText') {
         layout.reveal('stop');
      }

      app.openAnchor(anchor);
   }

   /** The markers of the threads about the open file, for the diff toolbar. */
   const openFileThreads = $derived(fileThreads(app.selectedThreadsAtPin, app.openFilePaths));

   const attachment = $derived(attachmentOf(app.selectedBinding));
   /** What Claude is doing on the selected tour, or null when the top bar has nothing to say about it or no tour is selected. */
   const generation = $derived(
      app.selectedTour === null ? null : generationStatus({ state: app.selectedTour.state, pin: app.selectedTour.pin, plan: app.selectedPlan?.plan, planWaits: app.selectedPlanWaits, attachment }),
   );

   /** Claude's mark on the selected changed file, or null for none. */
   const selectedMark = $derived(app.selectedFile === null ? null : (app.selectedFileMarks.get(app.selectedFile.path) ?? null));
   const writes: AnswerWrites = {
      onanswer: (checkId, input) => app.answerCheck(checkId, input),
      onsettype: (checkId, type) => app.setAnswerType(checkId, type),
      onclear: (checkId) => app.clearAnswer(checkId),
   };
   /** The open stop's answers, for its explanation, with their writes and the tour's send while the page shows the current stop version. */
   const answers = $derived<CheckAnswers>({
      byCheck: app.selectedStopAnswers,
      boundSessionId: app.selectedBinding?.sessionId ?? null,
      writes: app.shownVersion === null ? writes : null,
      send: app.shownVersion === null ? { unsent: app.unsentAnswers, offer: app.selectedControls?.offers.sendAnswers, state: app.selectedAnswerSend, onsend: () => app.sendAnswers('sendRow') } : null,
   });
   // The tour actions act on the current stop version, so an earlier version offers none.
   const act = $derived(app.shownVersion === null ? (kind: StopActionKind, text: string) => app.requestAction(kind, 'stop', text) : null);
   const explanation = new ExplanationView(
      () => app.selectedOpenStop ?? null,
      () => ({
         mode: settings.placement,
         threshold: settings.mixedThreshold,
         layout: settings.diffLayout,
         override: app.placementOverride,
         files: app.selectedFiles?.state === 'loaded' ? app.selectedFiles.list.files : null,
      }),
      {
         get threads() {
            return app.selectedThreads;
         },
         get answers() {
            return answers;
         },
         get commits() {
            return app.selectedPinCommits;
         },
         get controls() {
            return app.selectedControls;
         },
         get openedCheck() {
            return app.openedCheck;
         },
         // The stop-files view loads each file in its own view, so there an editor counts as able to show the block.
         get blockCanShow() {
            return app.showsStopFiles || !showsNoEditor(app.selectedText ?? app.selectedDiff);
         },
         hover,
         followReference: (reference) => app.followReference(reference),
         openThread,
         pick,
         requestExplain: (anchor) => app.requestAction('explain', anchor, ''),
         movePlacement: (placement) => app.movePlacement(placement),
         showCurrentVersion: () => app.showCurrentVersion(),
      },
   );

   onMount(() => {
      void app.start(settings.defaultViewMode);
      const restore = () => app.restoreNavigation(settings.defaultViewMode);
      window.addEventListener('popstate', restore);
      return () => {
         window.removeEventListener('popstate', restore);
         app.stop();
         settings.dispose();
      };
   });
</script>

{#snippet toolbar()}
   <!-- One row for the whole center, so the view modes and the passage navigation keep their place and their focus when the view below changes. -->
   <div class="flex shrink-0 items-center gap-3 border-b bg-card px-4 py-1.5">
      {#if app.selectedTourId !== null}
         <ViewModeSwitch mode={app.viewMode} onmode={(mode) => app.setViewMode(mode)} />
         <!-- A pending stop has no passages to step through yet, so the navigation waits for its content. -->
         {#if app.viewMode === 'stepThrough' && app.selectedStop?.stage === 'written'}
            <PassageNavigation
               passages={app.stepPassages}
               index={app.focusedPassageIndex}
               openFile={app.openFilePaths}
               onprevious={() => app.openPreviousPassage()}
               onnext={() => app.openNextPassage()}
            />
         {/if}
      {/if}
      {#if app.showsStopFiles}
         <p class="min-w-0 flex-1 truncate text-sm text-muted-foreground">
            {#if app.stopFiles !== null && app.stopFiles.length > 0}
               {app.stopFiles.length === 1 ? '1 file' : `${app.stopFiles.length} files`}
            {/if}
         </p>
         <DiffOptions
            layout={settings.diffLayout}
            onlayout={(diffLayout) => settings.setDiffLayout(diffLayout)}
            ignoreWhitespace={app.ignoreWhitespace}
            onignorewhitespace={(ignore) => app.setIgnoreWhitespace(ignore)}
            highlight={app.decorationSwitches.highlight}
            onhighlight={(highlight) => app.setHighlight(highlight)}
            dim={app.decorationSwitches.dim}
            ondim={(dim) => app.setDimming(dim)}
         />
      {:else if app.selectedText !== null}
         {@const text = app.selectedText}
         <FileHeading known={text} threads={openFileThreads} onopenthread={openThread} onask={() => pick({ kind: 'file', side: text.side, path: text.path }, null)} />
         <DiffOptions highlight={app.decorationSwitches.highlight} onhighlight={(highlight) => app.setHighlight(highlight)} dim={app.decorationSwitches.dim} ondim={(dim) => app.setDimming(dim)} />
      {:else if app.selectedFile !== null}
         {@const file = app.selectedFile}
         <DiffHeading
            path={file.path}
            oldPath={file.kind === 'renamed' ? file.oldPath : null}
            mark={selectedMark}
            threads={fileThreads(app.selectedThreadsAtPin, changedFilePaths(file))}
            onopenthread={openThread}
            onask={() => pick(changedFileAnchor(file), null)}
         />
         <DiffOptions
            layout={settings.diffLayout}
            onlayout={(diffLayout) => settings.setDiffLayout(diffLayout)}
            ignoreWhitespace={app.ignoreWhitespace}
            onignorewhitespace={(ignore) => app.setIgnoreWhitespace(ignore)}
            collapse={app.fileDecorations.passages.length > 0 ? app.fileDecorations.collapse : null}
            oncollapse={(collapse) => app.setCollapseToPassages(collapse)}
            highlight={app.decorationSwitches.highlight}
            onhighlight={(highlight) => app.setHighlight(highlight)}
            dim={app.decorationSwitches.dim}
            ondim={(dim) => app.setDimming(dim)}
         />
      {:else}
         <span class="flex-1"></span>
      {/if}
   </div>
{/snippet}

<!-- One scope for the page, so its tooltips share their delays and only one shows at a time. -->
<TooltipScope>
   {#if app.phase === 'ready'}
      <div class="flex h-full flex-col">
         <TopBar
            connection={app.connection}
            attachment={app.selectedTourId === null ? null : attachment}
            {generation}
            sendAnswers={app.selectedControls?.offers.sendAnswers}
            unsentAnswers={app.unsentAnswers}
            answerSend={app.selectedAnswerSend}
            onsendanswers={() => app.sendAnswers('topBar')}
            onprune={() => app.pruneClones()}
            {layout}
            {settings}
         >
            {#snippet picker()}
               <TourPicker
                  tours={app.listedTours}
                  hiddenCount={app.hiddenTourCount}
                  showHidden={app.showHiddenTours}
                  onshowhidden={(show) => app.setShowHiddenTours(show)}
                  onarchive={(tourId, archived) => app.setTourArchived(tourId, archived)}
                  ondelete={(tourId) => app.deleteTour(tourId)}
                  loaded={app.toursLoaded}
                  error={app.toursError}
                  selectedTourId={app.selectedTourId}
                  selectedTour={app.selectedTour}
                  onselect={(tourId) => app.selectTour(tourId, settings.defaultViewMode)}
               />
            {/snippet}
            {#snippet navigation()}
               {#if app.selectedTourId !== null}
                  <StopNavigation
                     plan={app.selectedPlan?.plan}
                     index={app.selectedStopIndex}
                     skipped={app.selectedSkippedStops}
                     summary={app.showsSummary}
                     summaryFollows={app.selectedSummaryFollows}
                     onprevious={() => app.openPreviousStop()}
                     onnext={() => app.openNextStop()}
                  />
               {/if}
            {/snippet}
            {#snippet iteration()}
               {#if app.selectedTour?.pin.state === 'pinned'}
                  <IterationCheck running={app.selectedIterationCheck.running} oncheck={() => void app.checkIteration()} />
               {/if}
            {/snippet}
         </TopBar>
         <IterationNotice
            available={app.selectedAvailableIteration}
            shown={app.selectedTour?.pin.state === 'pinned' ? app.selectedTour.pin.iteration : null}
            failure={app.selectedIterationCheck.failure}
         >
            {#snippet actions(available)}
               <!-- Each tour gets its own button, so an update in flight or a refusal for one tour never shows on the next. -->
               {#key app.selectedTourId}
                  <IterationUpdate iteration={available.pin.iteration} unavailable={disabledReason(app.selectedControls?.offers.updateIteration)} onupdate={() => app.updateIteration()} />
               {/key}
            {/snippet}
         </IterationNotice>
         <Workspace {layout} tourId={app.selectedTourId} defaultLeft={app.selectedTour?.pin.state === 'pinned' ? 'files' : 'plan'} diffTitle={app.showsSummary ? 'Summary' : 'Code'}>
            {#snippet files()}
               <!-- Each tour gets its own tree, so folders collapsed in one tour do not carry over to the next. -->
               {#key app.selectedTourId}
                  {@const tourId = app.selectedTourId ?? ''}
                  <FilesPanel
                     tour={app.selectedTour}
                     known={app.selectedFiles}
                     selectedPath={app.showsStopFiles ? app.stackFilePath : app.selectedFilePath}
                     selectedText={app.showsStopFiles ? app.stackText : app.selectedText}
                     fileMarks={app.selectedFileMarks}
                     reviewedPaths={app.selectedReviewedPaths}
                     reviewError={app.reviewError}
                     onreview={(path, reviewed) => app.setFileReviewed(path, reviewed)}
                     currentStop={app.selectedStop}
                     onselect={(path) => app.selectFile(path)}
                     onopen={(side, path) => app.openRepositoryFile(side, path)}
                     listDirectory={(side, path) => app.listDirectory(tourId, side, path)}
                  />
               {/key}
            {/snippet}
            {#snippet plan()}
               <!-- Each tour gets its own plan actions, so words or an order made for one tour never go to the next. -->
               <!-- A plan-first tour takes its level with the plan approval, and the server refuses a change of the level before it. -->
               {#key app.selectedTourId}
                  <PlanPanel
                     tourSelected={app.selectedTourId !== null}
                     known={app.selectedPlan}
                     error={app.selectedTourError}
                     {attachment}
                     currentStopId={app.selectedStop?.id ?? null}
                     onopen={(stopId) => app.openStop(stopId)}
                     summaryShown={app.showsSummary}
                     onopensummary={app.selectedSummaryFollows ? () => app.openSummary() : null}
                     threads={app.selectedThreads}
                     controls={app.selectedControls}
                     onaction={(action, text) => app.requestAction(action, 'tour', text)}
                     onopenthread={openThread}
                     skipped={app.selectedSkippedStops}
                     changed={app.selectedChangedStops}
                     onskip={(stopId, skipped) => app.setStopSkipped(stopId, skipped)}
                     skipsSaving={app.selectedSkipsSaving}
                     familiarity={app.selectedFamiliarity}
                     onfamiliarity={app.selectedTour?.planApproval === 'awaited' ? null : (level, text) => app.changeFamiliarity(level, text)}
                     planWaits={app.selectedPlanWaits}
                     onapprove={(stopIds, level, text) => app.approvePlan(stopIds, level, text)}
                  />
               {/key}
            {/snippet}
            {#snippet diff()}
               <main class="flex h-full flex-col bg-background" aria-label={app.showsSummary ? 'Summary' : app.showsStopFiles ? 'Stop files' : app.selectedText === null ? 'Diff' : 'File'}>
                  {@render toolbar()}
                  <div class="min-h-0 flex-1">
                     {#if app.showsSummary && app.selectedSummary !== null}
                        <SummaryPage
                           known={app.selectedSummary}
                           plan={app.selectedPlan?.plan ?? null}
                           threads={app.selectedThreads}
                           answers={app.selectedAnswers}
                           boundSessionId={app.selectedBinding?.sessionId ?? null}
                           onopenstop={(stopId) => openAnchor({ kind: 'stop', stopId })}
                           onopencheck={openCheck}
                           onopenthread={openThread}
                           onfollowreference={(reference) => app.followReference(reference)}
                           {hover}
                           atPin={(stopId, version) => app.summaryReferenceAtPin(stopId, version)}
                           pinCommits={app.selectedPinCommits}
                        >
                           {#snippet actions()}
                              <!-- Each tour gets its own button, so a request in flight or a refusal for one tour never shows on the next. -->
                              {#key app.selectedTourId}
                                 {@const pending = app.selectedControls?.offers.pending.summary ?? {}}
                                 <SummaryAction
                                    kind="summarize"
                                    unavailable={disabledReason(app.selectedControls?.offers.summarize)}
                                    waiting={threadsOf(app.selectedThreads, pending.summarize === undefined ? [] : [pending.summarize])[0] ?? null}
                                    onrequest={(text) => app.requestAction({ kind: 'summarize' }, 'tour', text)}
                                    onopenthread={openThread}
                                 />
                                 {#each followUpKinds as kind (kind)}
                                    {@const waitingFollowUp = pending[kind]}
                                    <SummaryAction
                                       {kind}
                                       unavailable={disabledReason(app.selectedControls?.offers.followUps[kind])}
                                       waiting={threadsOf(app.selectedThreads, waitingFollowUp === undefined ? [] : [waitingFollowUp])[0] ?? null}
                                       onrequest={(text) => app.requestAction({ kind }, 'tour', text)}
                                       onopenthread={openThread}
                                    />
                                 {/each}
                              {/key}
                           {/snippet}
                        </SummaryPage>
                     {:else if app.showsStopFiles && app.selectedStop !== null}
                        <!-- Each tour gets its own stack, so a load for one tour's file never lands in another tour's stack. -->
                        {#key app.selectedTourId}
                           {@const tourId = app.selectedTourId ?? ''}
                           <StopFiles
                              files={app.stopFiles}
                              stop={app.selectedStop}
                              fileMarks={app.selectedFileMarks}
                              layout={settings.diffLayout}
                              ignoreWhitespace={app.ignoreWhitespace}
                              focus={app.focus}
                              hovered={app.hoveredReference}
                              target={app.stackTarget}
                              ontop={(key) => app.setStackTop(key)}
                              {explanation}
                              threads={app.selectedThreadsAtPin}
                              onask={pick}
                              onopenthread={openThread}
                              loadDiff={(file, options) => app.fetchDiff(tourId, file, options)}
                              loadText={(side, path, includeLarge) => app.fetchText(tourId, side, path, includeLarge)}
                              pin={app.filesPin}
                           />
                        {/key}
                     {:else if app.selectedText === null}
                        <DiffView
                           path={app.selectedFile?.path ?? null}
                           oldPath={app.selectedFile?.kind === 'renamed' ? app.selectedFile.oldPath : null}
                           known={app.selectedDiff}
                           lineEndingsOnly={app.selectedFile?.lineEndingsOnly === true}
                           mark={selectedMark}
                           layout={settings.diffLayout}
                           focus={app.focus}
                           decorations={app.fileDecorations}
                           {explanation}
                           onask={pick}
                           onopenthread={openThread}
                           onloadlarge={() => app.loadLargeDiff()}
                        />
                     {:else}
                        <FileView
                           known={app.selectedText}
                           focus={app.focus}
                           decorations={app.fileDecorations}
                           {explanation}
                           onask={pick}
                           onopenthread={openThread}
                           onloadlarge={() => app.loadLargeText()}
                        />
                     {/if}
                  </div>
               </main>
            {/snippet}
            {#snippet stop()}
               {#if app.selectedStop}
                  <!-- Each stop gets its own panel, so the sections the reviewer opened or closed in one stop do not carry over to the next. -->
                  {#key app.selectedStop.id}
                     <StopPanel
                        stop={app.selectedStop}
                        view={explanation}
                        onopenpassage={(passage) => app.openPassage(passage)}
                        onaction={act}
                        controls={app.selectedControls}
                        versions={app.selectedStopVersions}
                        current={versionOf(app.currentStop)}
                        onopenversion={(number) => app.openVersion(number)}
                        skipped={app.selectedSkippedStops.has(app.selectedStop.id)}
                        onskip={(skipped) => app.setStopSkipped(app.selectedStop!.id, skipped)}
                        planWaits={app.selectedPlanWaits}
                     />
                  {/key}
               {:else if app.showsSummary}
                  <p class="px-4 py-3 text-muted-foreground">The summary covers the whole tour. Open a stop from it or from the plan, or go back to the last stop.</p>
               {:else}
                  <p class="px-4 py-3 text-muted-foreground">{app.selectedTourId === null ? 'Choose a tour to start.' : 'Open a stop from the plan, or go to the next stop.'}</p>
               {/if}
            {/snippet}
            {#snippet threads()}
               {#if app.selectedThreadsView === undefined || app.selectedThreadActions === undefined}
                  <p class="px-4 py-3 text-muted-foreground">Choose a tour to ask Claude about it.</p>
               {:else}
                  <!-- Each tour gets its own panel and actions, so a question typed in one tour is not sent in another. -->
                  {#key app.selectedTourId}
                     <ThreadActionsScope actions={app.selectedThreadActions}>
                        <ThreadsPanel
                           view={app.selectedThreadsView}
                           binding={app.selectedBinding}
                           controls={app.selectedControls}
                           plan={app.selectedPlan?.plan ?? null}
                           stop={app.selectedStop}
                           focusedPassage={app.focusedPassage}
                           onfollowreference={(reference) => app.followReference(reference)}
                           {hover}
                           opened={app.openedThread}
                           onopenanchor={openAnchor}
                           commits={app.selectedPinCommits}
                           messageStyle={settings.messageStyle}
                        />
                     </ThreadActionsScope>
                  {/key}
               {/if}
            {/snippet}
         </Workspace>
      </div>
   {:else}
      <main class="mx-auto my-16 max-w-160 px-4">
         <h1 class="my-5 text-3xl font-bold">Lotsen</h1>
         {#if app.phase === 'starting'}
            <p class="my-4">Connecting…</p>
         {:else if app.phase === 'signedOut'}
            <p class="my-4" role="alert">
               This page has no session, or its link has expired or was already used. Run <code>lotsen open</code> in a terminal to open lotsen.
            </p>
         {:else}
            <p class="my-4" role="alert">The lotsen server could not be reached. Run <code>lotsen open</code> in a terminal to open lotsen.</p>
         {/if}
      </main>
   {/if}
</TooltipScope>
