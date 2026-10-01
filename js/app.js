import { SrtParser, tokenizeLessonText } from './srt-parser.js';
import { parseIlpPackage, normalizeExercises } from './ilp-parser.js';
import { lessonStorage } from './storage.js';

class IntensiveListeningApp {
  constructor() {
    this.currentLesson = null;
    this.cues = [];
    this.duration = 0;
    this.currentTime = 0;
    this.isPlaying = false;
    this.singleSentenceLoop = false;
    this.repeatOnceStopAt = null;
    this.pausedOriginalCueIndex = null;
    
    // Cloze state
    this.revealedCloze = new Set();
    this.revealedMaterials = new Set();
    this.hiddenMaterials = new Set();
    this.showAllCloze = false;
    this.hideSubtitles = false;

    // Questions state
    this.selectedOptions = {}; // questionId -> optionIndex
    this.showAnswerState = {}; // questionId -> boolean

    // Auto-scroll follow
    this.isUserScrolling = false;
    this.userScrollTimeout = null;

    // DOM Elements
    this.audio = document.getElementById('audio-engine');
    this.dom = {
      app: document.getElementById('app'),
      appTitle: document.getElementById('app-title'),
      backBtn: document.getElementById('back-btn'),
      fileInfoBtn: document.getElementById('file-info-btn'),
      themeToggleBtn: document.getElementById('theme-toggle-btn'),
      btnOpenAudio: document.getElementById('btn-open-audio'),
      btnImportIlp: document.getElementById('btn-import-ilp'),
      btnInstallPwa: document.getElementById('btn-install-pwa'),
      mobileTabs: document.getElementById('mobile-tabs'),
      homeView: document.getElementById('home-view'),
      playerView: document.getElementById('player-view'),
      emptyState: document.getElementById('empty-state'),
      recentSection: document.getElementById('recent-lessons-section'),
      lessonList: document.getElementById('lesson-list'),
      btnEmptyOpenAudio: document.getElementById('btn-empty-open-audio'),
      btnEmptyImport: document.getElementById('btn-empty-import'),
      btnEmptyLoadSample: document.getElementById('btn-empty-load-sample'),
      
      // Question Card
      questionCard: document.getElementById('question-card'),
      noQuestionText: document.getElementById('no-question-text'),
      questionContent: document.getElementById('question-content'),
      currentQuestionTitle: document.getElementById('current-question-title'),
      questionOptions: document.getElementById('question-options'),
      btnToggleAnswer: document.getElementById('btn-toggle-answer'),
      answerDisplay: document.getElementById('answer-display'),

      // Controls
      seekSlider: document.getElementById('seek-slider'),
      currentTimeText: document.getElementById('current-time-text'),
      totalTimeText: document.getElementById('total-time-text'),
      btnPrevQuestion: document.getElementById('btn-prev-question'),
      btnPrevSentence: document.getElementById('btn-prev-sentence'),
      btnPlayToggle: document.getElementById('btn-play-toggle'),
      playIcon: document.getElementById('play-icon'),
      pauseIcon: document.getElementById('pause-icon'),
      btnNextSentence: document.getElementById('btn-next-sentence'),
      btnNextQuestion: document.getElementById('btn-next-question'),
      chkLoopLabel: document.getElementById('chk-loop-label'),
      chkLoopBox: document.getElementById('chk-loop-box'),
      chkHideSubtitlesLabel: document.getElementById('chk-hide-subtitles-label'),
      chkHideSubtitlesBox: document.getElementById('chk-hide-subtitles-box'),
      btnRepeatSentence: document.getElementById('btn-repeat-sentence'),
      btnToggleMaterialCloze: document.getElementById('btn-toggle-material-cloze'),
      btnToggleAllCloze: document.getElementById('btn-toggle-all-cloze'),
      questionStripContainer: document.getElementById('question-strip-container'),
      questionStrip: document.getElementById('question-strip'),

      // Transcript
      transcriptContainer: document.getElementById('transcript-container'),
      transcriptPane: document.getElementById('transcript-pane'),
      returnOriginalBtnWrap: document.getElementById('return-original-btn-wrap'),
      btnReturnOriginal: document.getElementById('btn-return-original'),
      cuePopover: document.getElementById('cue-popover'),
      popoverCueTime: document.getElementById('popover-cue-time'),
      popoverBtnPlay: document.getElementById('popover-btn-play'),
      popoverBtnPlayPause: document.getElementById('popover-btn-play-pause'),
      popoverBtnLoop: document.getElementById('popover-btn-loop'),
      popoverBtnClose: document.getElementById('popover-btn-close'),

      // Modals
      fileInfoModal: document.getElementById('file-info-modal'),
      btnCloseInfoModal: document.getElementById('btn-close-info-modal'),
      deleteModal: document.getElementById('delete-modal'),
      btnCancelDelete: document.getElementById('btn-cancel-delete'),
      btnConfirmDelete: document.getElementById('btn-confirm-delete'),
      alertModal: document.getElementById('alert-modal'),
      alertTitle: document.getElementById('alert-title'),
      alertMessage: document.getElementById('alert-message'),
      btnCloseAlert: document.getElementById('btn-close-alert'),

      // Mobile Mini Player
      miniSeekSlider: document.getElementById('mini-seek-slider'),
      miniCurrentTime: document.getElementById('mini-current-time'),
      miniTotalTime: document.getElementById('mini-total-time'),
      miniBtnPrev: document.getElementById('mini-btn-prev'),
      miniBtnPlay: document.getElementById('mini-btn-play'),
      miniPlayIcon: document.getElementById('mini-play-icon'),
      miniPauseIcon: document.getElementById('mini-pause-icon'),
      miniBtnNext: document.getElementById('mini-btn-next'),
      miniBtnRepeat: document.getElementById('mini-btn-repeat'),
      miniBtnLoop: document.getElementById('mini-btn-loop'),

      // Legal & Privacy
      btnShowLegal: document.getElementById('btn-show-legal'),
      legalModal: document.getElementById('legal-modal'),
      btnCloseLegal: document.getElementById('btn-close-legal'),
      legalContentAgreement: document.getElementById('legal-content-agreement'),
      legalContentPrivacy: document.getElementById('legal-content-privacy'),
      legalContentAudio: document.getElementById('legal-content-audio'),
      blockingLegalOverlay: document.getElementById('blocking-legal-overlay'),
      chkBlockingConsent: document.getElementById('chk-blocking-consent'),
      chkBlockingBox: document.getElementById('chk-blocking-box'),
      btnBlockingAccept: document.getElementById('btn-blocking-accept'),
      btnBlockingDecline: document.getElementById('btn-blocking-decline'),
      blockArticleAgreement: document.getElementById('block-article-agreement'),
      blockArticlePrivacy: document.getElementById('block-article-privacy'),
      blockArticleAudio: document.getElementById('block-article-audio'),

      // Ad Blocker Warning Modal
      adblockOverlay: document.getElementById('adblock-overlay'),
      btnAdblockRetry: document.getElementById('btn-adblock-retry'),
      btnAdblockIgnore: document.getElementById('btn-adblock-ignore'),
      btnAdblockGuideToggle: document.getElementById('btn-adblock-guide-toggle'),
      adblockGuideBox: document.getElementById('adblock-guide-box'),
      adblockRetryMsg: document.getElementById('adblock-retry-msg'),

      // Inputs & Overlays
      ilpFileInput: document.getElementById('ilp-file-input'),
      audioFileInput: document.getElementById('audio-file-input'),
      dropOverlay: document.getElementById('drop-overlay')
    };

    this.pendingDeleteId = null;
    this.pendingPopoverCueIndex = null;
    this.lastRenderedActiveIndex = -1;
    this.expandedSections = new Set();
    this.pendingPlayAction = null;
    this.telemetryWarningDismissed = false;
    this.isCheckingTelemetry = false;
    this.cachedTelemetryBlocked = undefined;
    this.audioSessionUnlocked = false;
    this.audioCtx = null;

    this.init();
  }

  init() {
    this.dom.app.setAttribute('data-view', 'home');
    this.bindEvents();
    this.loadSavedTheme();
    this.refreshRecentLessons();
    this.initTelemetryAndLegal();
    this.initPwaServiceWorker();
    this.initGlobalAudioUnlock();
  }

  // --- Theme Handling ---
  loadSavedTheme() {
    const saved = localStorage.getItem('theme_mode');
    if (saved === 'dark' || (!saved && window.matchMedia('(prefers-color-scheme: dark)').matches)) {
      document.documentElement.setAttribute('data-theme', 'dark');
    } else {
      document.documentElement.setAttribute('data-theme', 'light');
    }
  }

  toggleTheme() {
    const current = document.documentElement.getAttribute('data-theme');
    const next = current === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', next);
    localStorage.setItem('theme_mode', next);
  }

  // --- Event Bindings ---
  bindEvents() {
    // Theme toggle
    this.dom.themeToggleBtn.addEventListener('click', () => this.toggleTheme());

    // Navigation Back
    this.dom.backBtn.addEventListener('click', () => this.returnToHome());

    // File selection triggers
    this.dom.btnImportIlp.addEventListener('click', () => this.dom.ilpFileInput.click());
    this.dom.btnOpenAudio.addEventListener('click', () => this.dom.audioFileInput.click());
    this.dom.btnEmptyImport.addEventListener('click', () => this.dom.ilpFileInput.click());
    this.dom.btnEmptyOpenAudio.addEventListener('click', () => this.dom.audioFileInput.click());
    this.dom.btnEmptyLoadSample.addEventListener('click', () => this.loadSampleLesson());

    this.dom.ilpFileInput.addEventListener('change', (e) => this.handleFileSelect(e, 'ilp'));
    this.dom.audioFileInput.addEventListener('change', (e) => this.handleFileSelect(e, 'audio'));

    // Drag and Drop
    window.addEventListener('dragover', (e) => {
      e.preventDefault();
      this.dom.dropOverlay.classList.add('active');
    });
    window.addEventListener('dragleave', (e) => {
      if (e.relatedTarget === null) {
        this.dom.dropOverlay.classList.remove('active');
      }
    });
    window.addEventListener('drop', (e) => {
      e.preventDefault();
      this.dom.dropOverlay.classList.remove('active');
      const files = e.dataTransfer.files;
      if (files && files.length > 0) {
        this.processFile(files[0]);
      }
    });

    // Audio Engine Events
    this.audio.addEventListener('timeupdate', () => this.handleAudioTimeUpdate());
    this.audio.addEventListener('loadedmetadata', () => this.handleAudioLoadedMetadata());
    this.audio.addEventListener('play', () => this.setPlayState(true));
    this.audio.addEventListener('pause', () => this.setPlayState(false));
    this.audio.addEventListener('ended', () => this.handleAudioEnded());

    // Playback Controls
    this.dom.btnPlayToggle.addEventListener('click', () => this.togglePlayback());
    this.dom.seekSlider.addEventListener('input', (e) => this.handleSliderInput(e));
    this.dom.seekSlider.addEventListener('change', (e) => this.handleSliderChange(e));

    this.dom.btnPrevSentence.addEventListener('click', () => this.stepSentence(-1));
    this.dom.btnNextSentence.addEventListener('click', () => this.stepSentence(1));
    this.dom.btnPrevQuestion.addEventListener('click', () => this.stepQuestion(-1));
    this.dom.btnNextQuestion.addEventListener('click', () => this.stepQuestion(1));
    this.dom.btnRepeatSentence.addEventListener('click', () => this.repeatSentenceOnce());

    // Toggles
    this.dom.chkLoopLabel.addEventListener('click', (e) => {
      e.preventDefault();
      this.singleSentenceLoop = !this.singleSentenceLoop;
      this.updateLoopCheckboxUI();
    });

    this.dom.chkHideSubtitlesLabel.addEventListener('click', (e) => {
      e.preventDefault();
      this.hideSubtitles = !this.hideSubtitles;
      this.updateHideSubtitlesUI();
    });

    this.dom.btnToggleAllCloze.addEventListener('click', () => this.toggleAllCloze());
    this.dom.btnToggleMaterialCloze.addEventListener('click', () => this.toggleCurrentMaterialCloze());

    // Return to original cue
    this.dom.btnReturnOriginal.addEventListener('click', () => this.returnToOriginalCue());

    // Answer toggle in question card
    this.dom.btnToggleAnswer.addEventListener('click', () => this.toggleAnswerDisplay());

    // Popover actions (浮窗操作)
    this.dom.popoverBtnPlay.addEventListener('click', () => {
      if (this.pendingPopoverCueIndex !== null) {
        const idx = this.pendingPopoverCueIndex;
        this.hideCuePopover();
        this.playCue(idx, false);
      }
    });

    this.dom.popoverBtnPlayPause.addEventListener('click', () => {
      if (this.pendingPopoverCueIndex !== null) {
        const idx = this.pendingPopoverCueIndex;
        this.hideCuePopover();
        this.playCue(idx, true);
      }
    });

    if (this.dom.popoverBtnLoop) {
      this.dom.popoverBtnLoop.addEventListener('click', () => {
        if (this.pendingPopoverCueIndex !== null) {
          const idx = this.pendingPopoverCueIndex;
          this.singleSentenceLoop = true;
          this.updateLoopCheckboxUI();
          this.hideCuePopover();
          this.playCue(idx, false);
        }
      });
    }

    if (this.dom.popoverBtnClose) {
      this.dom.popoverBtnClose.addEventListener('click', (e) => {
        e.stopPropagation();
        this.hideCuePopover();
      });
    }

    // Dismiss popover when clicking outside of popover and cue rows
    document.addEventListener('click', (e) => {
      if (this.dom.cuePopover && !this.dom.cuePopover.contains(e.target) && !e.target.closest('.cue-row')) {
        this.hideCuePopover();
      }
    });

    // Detect user scroll in transcript container to pause auto-follow
    this.dom.transcriptContainer.addEventListener('scroll', () => {
      this.isUserScrolling = true;
      clearTimeout(this.userScrollTimeout);
      this.userScrollTimeout = setTimeout(() => {
        this.isUserScrolling = false;
      }, 4000);
    }, { passive: true });

    // Modals
    this.dom.fileInfoBtn.addEventListener('click', () => this.showFileInfoModal());
    this.dom.btnCloseInfoModal.addEventListener('click', () => this.hideFileInfoModal());

    this.dom.btnCancelDelete.addEventListener('click', () => {
      this.dom.deleteModal.style.display = 'none';
      this.pendingDeleteId = null;
    });
    this.dom.btnConfirmDelete.addEventListener('click', () => this.confirmDeleteLesson());
    this.dom.btnCloseAlert.addEventListener('click', () => {
      this.dom.alertModal.style.display = 'none';
    });

    // Header Review & Footer Legal Modal bindings
    document.querySelectorAll('.footer-legal-link, .legal-link').forEach(link => {
      link.addEventListener('click', (e) => {
        e.preventDefault();
        const tab = link.getAttribute('data-legal') || 'agreement';
        this.showLegalModal(tab);
      });
    });

    if (this.dom.btnShowLegal) {
      this.dom.btnShowLegal.addEventListener('click', () => this.showLegalModal());
    }
    if (this.dom.btnCloseLegal) {
      this.dom.btnCloseLegal.addEventListener('click', () => this.hideLegalModal());
    }

    document.querySelectorAll('.legal-tab').forEach(tab => {
      tab.addEventListener('click', (e) => {
        const target = e.currentTarget.getAttribute('data-legal');
        this.switchLegalTab(target);
      });
    });

    // Fullscreen Blocking Legal Overlay bindings (全屏阻断式协议弹窗)
    if (this.dom.chkBlockingConsent) {
      this.dom.chkBlockingConsent.addEventListener('click', (e) => {
        e.preventDefault();
        const isChecked = this.dom.chkBlockingConsent.classList.toggle('checked');
        if (this.dom.btnBlockingAccept) {
          this.dom.btnBlockingAccept.disabled = !isChecked;
        }
      });
    }

    if (this.dom.btnBlockingAccept) {
      this.dom.btnBlockingAccept.addEventListener('click', () => {
        if (!this.dom.chkBlockingConsent.classList.contains('checked')) {
          this.showAlert('请确认', '请先勾选同意《用户协议》、《隐私政策》并授权浏览器音频播放');
          return;
        }
        // 关键：在用户直接点击同意的手势事件中，同步激活并解锁浏览器音频硬件输出通道
        this.unlockAudioSession();
        localStorage.setItem('il_privacy_consent', 'true');
        if (this.dom.blockingLegalOverlay) {
          this.dom.blockingLegalOverlay.style.display = 'none';
        }
      });
    }

    if (this.dom.btnBlockingDecline) {
      this.dom.btnBlockingDecline.addEventListener('click', () => {
        this.showAlert('温馨提示', '本在线播放器需要用户同意《用户协议》与音频播放权限方可正常使用。如您不同意，可关闭本网页标签。');
      });
    }

    document.querySelectorAll('.blocking-tab-btn').forEach(btn => {
      btn.addEventListener('click', (e) => {
        const tab = e.currentTarget.getAttribute('data-tab');
        document.querySelectorAll('.blocking-tab-btn').forEach(b => b.classList.remove('active'));
        e.currentTarget.classList.add('active');
        if (this.dom.blockArticleAgreement) {
          this.dom.blockArticleAgreement.style.display = (tab === 'agreement' ? 'block' : 'none');
        }
        if (this.dom.blockArticlePrivacy) {
          this.dom.blockArticlePrivacy.style.display = (tab === 'privacy' ? 'block' : 'none');
        }
        if (this.dom.blockArticleAudio) {
          this.dom.blockArticleAudio.style.display = (tab === 'audio' ? 'block' : 'none');
        }
      });
    });

    // Fullscreen Ad Blocker Warning Modal bindings
    if (this.dom.btnAdblockRetry) {
      this.dom.btnAdblockRetry.addEventListener('click', () => this.handleAdblockRetry());
    }
    if (this.dom.btnAdblockIgnore) {
      this.dom.btnAdblockIgnore.addEventListener('click', () => this.handleAdblockIgnore());
    }
    if (this.dom.btnAdblockGuideToggle) {
      this.dom.btnAdblockGuideToggle.addEventListener('click', () => {
        if (this.dom.adblockGuideBox) {
          const isHidden = this.dom.adblockGuideBox.style.display === 'none';
          this.dom.adblockGuideBox.style.display = isHidden ? 'block' : 'none';
          this.dom.btnAdblockGuideToggle.textContent = isHidden ? '收起白名单指引' : '白名单设置指引';
        }
      });
    }

    // Mobile tabs switcher
    this.dom.mobileTabs.addEventListener('click', (e) => {
      const tab = e.target.closest('.mobile-tab');
      if (!tab) return;
      document.querySelectorAll('.mobile-tab').forEach(t => t.classList.remove('active'));
      tab.classList.add('active');
      const tabName = tab.getAttribute('data-tab');
      this.dom.playerView.setAttribute('data-tab', tabName);
    });

    // Mobile Mini Player controls
    if (this.dom.miniBtnPlay) {
      this.dom.miniBtnPlay.addEventListener('click', () => this.togglePlayback());
      this.dom.miniBtnPrev.addEventListener('click', () => this.stepSentence(-1));
      this.dom.miniBtnNext.addEventListener('click', () => this.stepSentence(1));
      this.dom.miniBtnRepeat.addEventListener('click', () => this.repeatSentenceOnce());
      this.dom.miniBtnLoop.addEventListener('click', () => {
        this.singleSentenceLoop = !this.singleSentenceLoop;
        this.updateLoopCheckboxUI();
      });
      this.dom.miniSeekSlider.addEventListener('input', (e) => this.handleSliderInput(e));
      this.dom.miniSeekSlider.addEventListener('change', (e) => this.handleSliderChange(e));
    }

    // Keyboard Shortcuts
    window.addEventListener('keydown', (e) => {
      if (e.code === 'Escape') {
        this.hideCuePopover();
      }
      if (e.code === 'Space' && e.target.tagName !== 'INPUT' && !e.target.closest('#blocking-legal-overlay')) {
        e.preventDefault();
        this.togglePlayback();
      }
    });
  }

  // --- File Processing ---
  async handleFileSelect(e, type) {
    const file = e.target.files?.[0];
    if (!file) return;
    e.target.value = '';
    await this.processFile(file, type);
  }

  async processFile(file, hintType = null) {
    const isIlp = file.name.toLowerCase().endsWith('.ilp') || hintType === 'ilp';
    try {
      if (isIlp) {
        const parsed = await parseIlpPackage(file);
        const lessonRecord = {
          id: parsed.manifest.packageUuid || ('ilp-' + Date.now()),
          manifest: parsed.manifest,
          cues: parsed.cues,
          audioBlob: parsed.audioBlob,
          audioUrl: parsed.audioUrl,
          lastOpenedAt: Date.now(),
          progressPosition: 0,
          revealedCloze: []
        };
        await lessonStorage.saveLesson(lessonRecord);
        this.reportTelemetryEvent('import_ilp', { title: parsed.manifest.title, cuesCount: parsed.cues.length });
        await this.loadLesson(lessonRecord);
      } else {
        // Standalone Audio File
        const audioUrl = URL.createObjectURL(file);
        const nameWithoutExt = file.name.replace(/\.[^/.]+$/, '');
        this.reportTelemetryEvent('open_audio', { filename: file.name });
        const standaloneRecord = {
          id: 'audio-' + Date.now(),
          manifest: {
            title: nameWithoutExt,
            packageUuid: 'audio-' + Date.now(),
            packageVersion: 1,
            audioPath: file.name,
            transcriptPath: '',
            durationMs: 0,
            exercises: { materials: [], questions: [], clozeWordIndexes: {} }
          },
          cues: [],
          audioBlob: file,
          audioUrl,
          lastOpenedAt: Date.now(),
          progressPosition: 0,
          revealedCloze: []
        };
        await this.loadLesson(standaloneRecord);
      }
    } catch (err) {
      this.showAlert('文件解析失败', err.message || err);
    }
  }

  async loadSampleLesson() {
    try {
      const resp = await fetch('sample.ilp');
      if (!resp.ok) {
        throw new Error('未找到示例精听包 sample.ilp');
      }
      const blob = await resp.blob();
      const parsed = await parseIlpPackage(blob);
      const lessonRecord = {
        id: parsed.manifest.packageUuid || 'sample-lesson',
        manifest: parsed.manifest,
        cues: parsed.cues,
        audioBlob: parsed.audioBlob,
        audioUrl: parsed.audioUrl,
        lastOpenedAt: Date.now(),
        progressPosition: 0,
        revealedCloze: []
      };
      await lessonStorage.saveLesson(lessonRecord);
      await this.loadLesson(lessonRecord);
    } catch (err) {
      this.showAlert('加载示例失败', err.message || err);
    }
  }

  // --- Lesson Loading & View Transition ---
  async loadLesson(lesson) {
    if (!lesson.manifest && lesson.manifestRaw) {
      lesson.manifest = lesson.manifestRaw;
    }
    if (lesson.manifest) {
      lesson.manifest.exercises = normalizeExercises(lesson.manifest.exercises || {});
    }

    this.currentLesson = lesson;
    this.cues = lesson.cues || [];
    this.duration = (lesson.manifest?.durationMs ? lesson.manifest.durationMs / 1000 : 0);
    this.currentTime = lesson.progressPosition || 0;
    this.revealedCloze = new Set(lesson.revealedCloze || []);
    this.revealedMaterials.clear();
    this.hiddenMaterials.clear();
    this.showAllCloze = false;
    this.hideSubtitles = false;
    this.pausedOriginalCueIndex = null;
    this.lastRenderedActiveIndex = -1;

    // Set UI Header & View State
    this.dom.app.setAttribute('data-view', 'player');
    this.dom.appTitle.textContent = lesson.manifest.title;
    this.dom.appTitle.title = lesson.manifest.title;
    this.dom.backBtn.style.display = 'inline-flex';
    this.dom.fileInfoBtn.style.display = 'inline-flex';

    // Switch View
    this.dom.homeView.style.display = 'none';
    this.dom.playerView.style.display = 'flex';

    // Prepare Audio
    let audioUrl = lesson.audioUrl;
    if (!audioUrl && lesson.audioBlob) {
      audioUrl = URL.createObjectURL(lesson.audioBlob);
      lesson.audioUrl = audioUrl;
    }
    this.audio.volume = 1.0;
    this.audio.muted = false;
    this.audio.src = audioUrl;
    this.updateMediaSession(lesson);

    // Render Transcript & Question Strip
    this.renderTranscriptPane();
    this.renderQuestionStrip();
    this.updateControlsState();

    // Restore saved position
    if (this.currentTime > 0) {
      this.audio.currentTime = this.currentTime;
    }

    this.reportTelemetryEvent('open_lesson', {
      title: lesson.manifest?.title || 'Untitled',
      duration: this.duration,
      cuesCount: this.cues.length
    });

    // Set mobile tab to question by default
    this.dom.playerView.setAttribute('data-tab', 'question');
    document.querySelectorAll('.mobile-tab').forEach(t => {
      t.classList.toggle('active', t.getAttribute('data-tab') === 'question');
    });

    this.updateActiveCue(this.currentTime, true);
  }

  returnToHome() {
    this.dom.app.setAttribute('data-view', 'home');
    this.audio.pause();
    this.currentLesson = null;
    this.dom.playerView.style.display = 'none';
    this.dom.homeView.style.display = 'flex';
    this.dom.backBtn.style.display = 'none';
    this.dom.fileInfoBtn.style.display = 'none';
    this.dom.appTitle.textContent = 'Intensive Listening';
    this.refreshRecentLessons();
  }

  async refreshRecentLessons() {
    const lessons = await lessonStorage.getAllLessons();
    if (!lessons || lessons.length === 0) {
      this.dom.emptyState.style.display = 'flex';
      this.dom.recentSection.style.display = 'none';
      return;
    }

    this.dom.emptyState.style.display = 'none';
    this.dom.recentSection.style.display = 'block';
    this.dom.lessonList.innerHTML = '';

    for (const item of lessons) {
      const row = document.createElement('div');
      row.className = 'lesson-row';

      const durationSec = (item.durationMs ? item.durationMs / 1000 : 0);
      const formattedDur = SrtParser.formatDuration(durationSec);
      const questionsCount = item.manifestRaw?.exercises?.questions?.length || 0;
      const progressRatio = durationSec > 0 ? Math.min(1, (item.progressPosition || 0) / durationSec) : 0;
      const percent = Math.round(progressRatio * 100);

      row.innerHTML = `
        <div class="lesson-row-info">
          <div class="lesson-row-title">${this.escapeHtml(item.title)}</div>
          <div class="lesson-row-meta">
            <span>时长: ${formattedDur}</span>
            <span>${questionsCount} 道题目</span>
            <div class="progress-track">
              <div class="progress-fill" style="width: ${percent}%;"></div>
            </div>
            <span>${percent}%</span>
          </div>
        </div>
        <div class="lesson-row-actions">
          <button class="fluent-btn sm filled btn-open-item">打开</button>
          <button class="icon-btn btn-delete-item" title="删除记录">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <polyline points="3 6 5 6 21 6"></polyline>
              <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
            </svg>
          </button>
        </div>
      `;

      row.querySelector('.btn-open-item').addEventListener('click', (e) => {
        e.stopPropagation();
        this.loadLesson(item);
      });

      row.querySelector('.btn-delete-item').addEventListener('click', (e) => {
        e.stopPropagation();
        this.askDeleteLesson(item.id);
      });

      row.addEventListener('click', () => {
        this.loadLesson(item);
      });

      this.dom.lessonList.appendChild(row);
    }
  }

  askDeleteLesson(id) {
    this.pendingDeleteId = id;
    this.dom.deleteModal.style.display = 'flex';
  }

  async confirmDeleteLesson() {
    if (!this.pendingDeleteId) return;
    await lessonStorage.deleteLesson(this.pendingDeleteId);
    this.pendingDeleteId = null;
    this.dom.deleteModal.style.display = 'none';
    this.refreshRecentLessons();
  }

  // --- Audio Event Handlers ---
  handleAudioLoadedMetadata() {
    if (this.audio.duration && !isNaN(this.audio.duration)) {
      this.duration = this.audio.duration;
    }
    const formatted = SrtParser.formatDuration(this.duration);
    this.dom.totalTimeText.textContent = formatted;
    if (this.dom.miniTotalTime) {
      this.dom.miniTotalTime.textContent = formatted;
    }
    this.updateControlsState();
  }

  handleAudioTimeUpdate() {
    this.currentTime = this.audio.currentTime;
    
    // Repeat sentence check
    if (this.repeatOnceStopAt !== null && this.currentTime >= this.repeatOnceStopAt) {
      this.repeatOnceStopAt = null;
      this.audio.pause();
    }

    // Single sentence loop check
    const activeCue = this.getActiveCue(this.currentTime);
    if (this.singleSentenceLoop && activeCue && this.currentTime >= activeCue.end) {
      this.audio.currentTime = activeCue.start;
      return;
    }

    // Update Slider & Text
    if (!this.isSeeking) {
      const progressRatio = this.duration > 0 ? (this.currentTime / this.duration) : 0;
      const progressPercent = Math.min(100, Math.max(0, progressRatio * 100));
      const sliderVal = Math.round(progressRatio * 1000);
      const timeStr = SrtParser.formatDuration(this.currentTime);

      this.dom.seekSlider.value = sliderVal;
      this.dom.seekSlider.style.setProperty('--progress', `${progressPercent}%`);
      this.dom.currentTimeText.textContent = timeStr;

      if (this.dom.miniSeekSlider) {
        this.dom.miniSeekSlider.value = sliderVal;
        this.dom.miniSeekSlider.style.setProperty('--progress', `${progressPercent}%`);
        this.dom.miniCurrentTime.textContent = timeStr;
      }
    }

    this.updateActiveCue(this.currentTime);
    this.saveProgressDebounced();
  }

  handleAudioEnded() {
    this.setPlayState(false);
  }

  saveProgressDebounced() {
    if (!this.currentLesson || !this.currentLesson.id) return;
    if (this._progressTimer) clearTimeout(this._progressTimer);
    this._progressTimer = setTimeout(() => {
      lessonStorage.updateProgress(this.currentLesson.id, {
        position: this.currentTime,
        revealedCloze: Array.from(this.revealedCloze)
      });
    }, 1000);
  }

  setPlayState(playing) {
    this.isPlaying = playing;
    this.dom.playIcon.style.display = playing ? 'none' : 'block';
    this.dom.pauseIcon.style.display = playing ? 'block' : 'none';
    if (this.dom.miniPlayIcon) {
      this.dom.miniPlayIcon.style.display = playing ? 'none' : 'block';
      this.dom.miniPauseIcon.style.display = playing ? 'block' : 'none';
    }
    if (playing) {
      this.pausedOriginalCueIndex = null;
      this.dom.returnOriginalBtnWrap.style.display = 'none';
    }
  }

  togglePlayback() {
    if (!this.audio.src) return;
    if (this.audio.paused) {
      this.ensureTelemetryBeforePlay(() => {
        this.safePlayAudio();
      });
    } else {
      this.audio.pause();
    }
  }

  handleSliderInput(e) {
    this.isSeeking = true;
    const val = parseFloat(e.target.value) / 1000;
    const targetTime = val * this.duration;
    const timeStr = SrtParser.formatDuration(targetTime);
    this.dom.currentTimeText.textContent = timeStr;
    this.dom.seekSlider.value = e.target.value;
    this.dom.seekSlider.style.setProperty('--progress', `${val * 100}%`);
    if (this.dom.miniSeekSlider) {
      this.dom.miniSeekSlider.value = e.target.value;
      this.dom.miniSeekSlider.style.setProperty('--progress', `${val * 100}%`);
      this.dom.miniCurrentTime.textContent = timeStr;
    }
  }

  handleSliderChange(e) {
    const val = parseFloat(e.target.value) / 1000;
    const targetTime = val * this.duration;
    this.audio.currentTime = targetTime;
    this.isSeeking = false;
  }

  // --- Cue & Exercise Navigation ---
  getActiveCueIndex(position) {
    if (!this.cues || this.cues.length === 0) return -1;
    for (let i = this.cues.length - 1; i >= 0; i--) {
      if (position >= this.cues[i].start) return i;
    }
    return 0;
  }

  getActiveCue(position) {
    const idx = this.getActiveCueIndex(position);
    return idx >= 0 ? this.cues[idx] : null;
  }

  updateActiveCue(position, force = false) {
    const activeIndex = this.getActiveCueIndex(position);
    if (activeIndex === this.lastRenderedActiveIndex && !force) return;
    this.lastRenderedActiveIndex = activeIndex;

    // Highlight cue rows
    document.querySelectorAll('.cue-row').forEach(row => {
      const idx = parseInt(row.getAttribute('data-cue-index'), 10);
      row.classList.toggle('active', idx === activeIndex);
    });

    // Highlight transcript group cards
    document.querySelectorAll('.transcript-card').forEach(card => {
      const cueIndices = (card.getAttribute('data-cues') || '').split(',').map(s => parseInt(s, 10));
      card.classList.toggle('active', cueIndices.includes(activeIndex));
    });

    // Auto-expand any expander (unassigned or repeated) containing this active cue
    if (activeIndex >= 0) {
      this.syncPlaybackExpanders(activeIndex);
    }

    // Auto-scroll follow
    if (!this.isUserScrolling && activeIndex >= 0) {
      this.scrollToActiveCue(activeIndex);
    }

    // Update active Material & Question
    this.updateActiveMaterialQuestion(activeIndex);
  }

  syncPlaybackExpanders(activeIndex) {
    if (activeIndex < 0) return;

    // 1. Unassigned expander cards
    document.querySelectorAll('.transcript-card[data-is-expander="true"]').forEach(card => {
      const cues = (card.getAttribute('data-cues') || '').split(',').map(Number);
      if (cues.includes(activeIndex)) {
        const list = card.querySelector('.cue-list');
        const header = card.querySelector('.transcript-card-header');
        if (list && list.classList.contains('collapsed')) {
          list.classList.remove('collapsed');
          header?.classList.add('expanded');
          const key = card.getAttribute('data-key');
          if (key) this.expandedSections.add(key);
        }
      }
    });

    // 2. Repeated cues sections
    document.querySelectorAll('.repeated-cues-section').forEach(section => {
      const cues = (section.getAttribute('data-repeated-cues') || '').split(',').map(Number);
      if (cues.includes(activeIndex)) {
        const list = section.querySelector('.repeated-cue-list');
        const header = section.querySelector('.expander-header');
        if (list && list.classList.contains('collapsed')) {
          list.classList.remove('collapsed');
          header?.classList.add('expanded');
          const key = section.getAttribute('data-key');
          if (key) this.expandedSections.add(key);
        }
      }
    });
  }

  scrollToActiveCue(cueIndex) {
    const row = document.querySelector(`.cue-row[data-cue-index="${cueIndex}"]`);
    if (!row) return;
    row.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }

  updateActiveMaterialQuestion(cueIndex) {
    if (!this.currentLesson || !this.currentLesson.manifest?.exercises) {
      this.dom.noQuestionText.style.display = 'block';
      this.dom.questionContent.style.display = 'none';
      return;
    }

    const exercises = this.currentLesson.manifest.exercises;
    const material = exercises.materialForCue(cueIndex);

    // Update Question Index Strip Active state
    document.querySelectorAll('.strip-item').forEach(item => {
      const mid = item.getAttribute('data-material-id');
      item.classList.toggle('active', material && mid === material.id);
    });

    // Update Material Cloze button visibility
    if (material) {
      this.dom.btnToggleMaterialCloze.style.display = 'inline-block';
      const isMaterialRevealed = this.revealedMaterials.has(material.id);
      this.dom.btnToggleMaterialCloze.textContent = isMaterialRevealed ? '隐藏本段挖空' : '显示本段挖空';
    } else {
      this.dom.btnToggleMaterialCloze.style.display = 'none';
    }

    if (!material || !material.questionIds || material.questionIds.length === 0) {
      this.dom.noQuestionText.style.display = 'block';
      this.dom.questionContent.style.display = 'none';
      return;
    }

    const questions = exercises.questionsForMaterial(material);
    if (!questions || questions.length === 0) {
      this.dom.noQuestionText.style.display = 'block';
      this.dom.questionContent.style.display = 'none';
      return;
    }

    // Show first question for the material
    const currentQ = questions[0];
    this.dom.noQuestionText.style.display = 'none';
    this.dom.questionContent.style.display = 'block';
    this.dom.currentQuestionTitle.textContent = `第 ${currentQ.number} 题  ${currentQ.title}`;

    // Options rendering
    this.dom.questionOptions.innerHTML = '';
    const selectedOpt = this.selectedOptions[currentQ.id];

    currentQ.options.forEach((optText, optIdx) => {
      const optItem = document.createElement('div');
      const letter = String.fromCharCode(65 + optIdx);
      const isSelected = selectedOpt === optIdx;

      optItem.className = `option-item ${isSelected ? 'selected' : ''}`;
      optItem.innerHTML = `
        <div class="option-radio">
          <div class="option-radio-dot"></div>
        </div>
        <span>${letter}. ${this.escapeHtml(optText)}</span>
      `;

      optItem.addEventListener('click', () => {
        this.selectedOptions[currentQ.id] = optIdx;
        this.updateActiveMaterialQuestion(cueIndex);
      });

      this.dom.questionOptions.appendChild(optItem);
    });

    // Answer display
    if (currentQ.answerIndex !== null && currentQ.answerIndex !== undefined) {
      this.dom.btnToggleAnswer.style.display = 'inline-block';
      const isShow = !!this.showAnswerState[currentQ.id];
      this.dom.btnToggleAnswer.textContent = isShow ? '隐藏答案' : '显示答案';
      this.dom.answerDisplay.style.display = isShow ? 'inline' : 'none';
      const ansLetter = String.fromCharCode(65 + currentQ.answerIndex);
      this.dom.answerDisplay.textContent = `答案：${ansLetter}`;
    } else {
      this.dom.btnToggleAnswer.style.display = 'none';
      this.dom.answerDisplay.style.display = 'none';
    }
  }

  toggleAnswerDisplay() {
    const activeIndex = this.getActiveCueIndex(this.currentTime);
    const exercises = this.currentLesson?.manifest?.exercises;
    if (!exercises) return;
    const material = exercises.materialForCue(activeIndex);
    if (!material) return;
    const questions = exercises.questionsForMaterial(material);
    if (!questions || questions.length === 0) return;

    const q = questions[0];
    this.showAnswerState[q.id] = !this.showAnswerState[q.id];
    this.updateActiveMaterialQuestion(activeIndex);
  }

  stepSentence(delta) {
    if (!this.cues || this.cues.length === 0) return;
    const currentIdx = this.getActiveCueIndex(this.currentTime);
    let targetIdx = currentIdx;
    if (delta < 0) {
      if (this.currentTime - this.cues[currentIdx].start > 1.5 || currentIdx <= 0) {
        targetIdx = currentIdx;
      } else {
        targetIdx = Math.max(0, currentIdx - 1);
      }
    } else {
      targetIdx = Math.min(this.cues.length - 1, currentIdx + 1);
    }
    this.selectCue(targetIdx);
    this.scrollToActiveCue(targetIdx);
  }

  stepQuestion(delta) {
    const exercises = this.currentLesson?.manifest?.exercises;
    if (!exercises || !exercises.materials || exercises.materials.length === 0) return;
    const validMaterials = exercises.materials.filter(m => m.questionIds && m.questionIds.length > 0);
    if (validMaterials.length === 0) return;

    const currentIdx = this.getActiveCueIndex(this.currentTime);
    const currentMatIdx = validMaterials.findIndex(m => m.cueIndexes.includes(currentIdx) || m.leadInCueIndexes?.includes(currentIdx));

    let targetIdx = 0;
    if (currentMatIdx >= 0) {
      targetIdx = Math.min(validMaterials.length - 1, Math.max(0, currentMatIdx + delta));
    }
    const targetMat = validMaterials[targetIdx];
    if (targetMat) {
      const cueIdx = (targetMat.leadInCueIndexes && targetMat.leadInCueIndexes.length > 0) ? targetMat.leadInCueIndexes[0] : targetMat.cueIndexes[0];
      if (cueIdx !== undefined && this.cues[cueIdx]) {
        this.selectCue(cueIdx);
        this.scrollToActiveCue(cueIdx);
      }
    }
  }

  repeatSentenceOnce() {
    const cue = this.getActiveCue(this.currentTime);
    if (!cue) return;
    this.ensureTelemetryBeforePlay(() => {
      this.audio.currentTime = cue.start;
      this.repeatOnceStopAt = cue.end;
      this.safePlayAudio();
    });
  }

  playCue(cueIndex, pauseAfter) {
    if (cueIndex < 0 || cueIndex >= this.cues.length) return;
    this.ensureTelemetryBeforePlay(() => {
      const cue = this.cues[cueIndex];
      this.audio.currentTime = cue.start;
      if (pauseAfter) {
        this.repeatOnceStopAt = cue.end;
      } else {
        this.repeatOnceStopAt = null;
      }
      this.safePlayAudio();
    });
  }

  returnToOriginalCue() {
    if (this.pausedOriginalCueIndex !== null && this.pausedOriginalCueIndex < this.cues.length) {
      const origIndex = this.pausedOriginalCueIndex;
      const cue = this.cues[origIndex];
      this.audio.currentTime = cue.start;
      this.currentTime = cue.start;
      this.updateActiveCue(cue.start, true);
      this.pausedOriginalCueIndex = null;
      this.dom.returnOriginalBtnWrap.style.display = 'none';
      this.hideCuePopover();
      this.scrollToActiveCue(origIndex);
    }
  }

  // --- Cloze Handling ---
  toggleClozeWord(cueIndex, wordIndex) {
    const key = `${cueIndex}:${wordIndex}`;
    if (this.revealedCloze.has(key)) {
      this.revealedCloze.delete(key);
    } else {
      this.revealedCloze.add(key);
    }
    this.renderTranscriptPane();
    this.saveProgressDebounced();
  }

  toggleAllCloze() {
    this.showAllCloze = !this.showAllCloze;
    this.dom.btnToggleAllCloze.textContent = this.showAllCloze ? '隐藏全部挖空' : '显示全部挖空';
    this.renderTranscriptPane();
  }

  toggleCurrentMaterialCloze() {
    const activeIndex = this.getActiveCueIndex(this.currentTime);
    const exercises = this.currentLesson?.manifest?.exercises;
    if (!exercises) return;
    const material = exercises.materialForCue(activeIndex);
    if (!material) return;

    if (this.revealedMaterials.has(material.id)) {
      this.revealedMaterials.delete(material.id);
      this.hiddenMaterials.add(material.id);
    } else {
      this.hiddenMaterials.delete(material.id);
      this.revealedMaterials.add(material.id);
    }
    this.renderTranscriptPane();
    this.updateActiveMaterialQuestion(activeIndex);
  }

  updateLoopCheckboxUI() {
    this.dom.chkLoopBox.parentElement.classList.toggle('checked', this.singleSentenceLoop);
    if (this.dom.miniBtnLoop) {
      this.dom.miniBtnLoop.classList.toggle('filled', this.singleSentenceLoop);
    }
  }

  updateHideSubtitlesUI() {
    this.dom.chkHideSubtitlesBox.parentElement.classList.toggle('checked', this.hideSubtitles);
    this.dom.transcriptPane.classList.toggle('masked', this.hideSubtitles);
  }

  updateControlsState() {
    const hasMedia = !!this.currentLesson;
    const hasTranscript = this.cues.length > 0;
    this.dom.btnPrevSentence.disabled = !hasTranscript;
    this.dom.btnNextSentence.disabled = !hasTranscript;
    this.dom.btnRepeatSentence.disabled = !hasTranscript;
    this.dom.btnPlayToggle.disabled = !hasMedia;
    this.dom.seekSlider.disabled = !hasMedia;
  }

  // --- Rendering UI Components ---
  renderQuestionStrip() {
    const exercises = this.currentLesson?.manifest?.exercises;
    if (!exercises || !exercises.materials || exercises.materials.length === 0) {
      this.dom.questionStripContainer.style.display = 'none';
      return;
    }

    const validMaterials = exercises.materials.filter(m => m.questionIds && m.questionIds.length > 0);
    if (validMaterials.length === 0) {
      this.dom.questionStripContainer.style.display = 'none';
      return;
    }

    this.dom.questionStripContainer.style.display = 'flex';
    this.dom.questionStrip.innerHTML = '';

    validMaterials.forEach(m => {
      const questions = exercises.questionsForMaterial(m);
      const numbers = questions.map(q => q.number).filter(n => n > 0).sort((a, b) => a - b);
      let label = '听力材料';
      if (numbers.length === 1) {
        label = `第 ${numbers[0]} 题`;
      } else if (numbers.length > 1) {
        const consecutive = (numbers[numbers.length - 1] - numbers[0] + 1 === numbers.length);
        label = `${consecutive ? `${numbers[0]}–${numbers[numbers.length - 1]}` : numbers.join('、')} 题`;
      }

      const stripBtn = document.createElement('div');
      stripBtn.className = 'strip-item';
      stripBtn.setAttribute('data-material-id', m.id);
      stripBtn.textContent = label;

      stripBtn.addEventListener('click', () => {
        const cueIdx = (m.leadInCueIndexes && m.leadInCueIndexes.length > 0) ? m.leadInCueIndexes[0] : m.cueIndexes[0];
        if (cueIdx !== undefined && this.cues[cueIdx]) {
          this.selectCue(cueIdx);
          this.scrollToActiveCue(cueIdx);
        }
      });

      this.dom.questionStrip.appendChild(stripBtn);
    });
  }

  renderTranscriptPane() {
    if (!this.cues || this.cues.length === 0) {
      this.dom.transcriptPane.innerHTML = '<div style="color: var(--text-secondary); text-align: center; padding: 40px 0;">该音频无配套字幕</div>';
      return;
    }

    const exercises = this.currentLesson?.manifest?.exercises;
    const clozeMap = exercises?.clozeWordIndexes || {};

    // Group cues by material
    const groups = [];
    for (let i = 0; i < this.cues.length; i++) {
      const materialIndex = exercises ? exercises.materialIndexForCue(i) : null;
      if (groups.length === 0 || groups[groups.length - 1].materialIndex !== materialIndex) {
        groups.push({
          materialIndex,
          cueIndexes: []
        });
      }
      groups[groups.length - 1].cueIndexes.push(i);
    }

    const activeIdx = this.getActiveCueIndex(this.currentTime);
    this.dom.transcriptPane.innerHTML = '';

    groups.forEach(group => {
      const isUnassigned = (group.materialIndex === null);
      const material = (!isUnassigned && exercises) ? exercises.materials[group.materialIndex] : null;
      const questions = material ? exercises.questionsForMaterial(material) : [];
      const questionNums = questions.map(q => q.number).filter(n => n > 0).join('、');

      let title = '题前提示';
      if (material) {
        title = questionNums ? `第 ${questionNums} 题` : '听力材料';
      }

      const card = document.createElement('div');
      card.className = 'transcript-card';
      card.setAttribute('data-cues', group.cueIndexes.join(','));

      const unassignedKey = 'unassigned-' + group.cueIndexes[0];
      const isUnassignedActive = group.cueIndexes.includes(activeIdx);
      const isUnassignedExpanded = isUnassignedActive || this.expandedSections.has(unassignedKey);

      // Header
      const header = document.createElement('div');
      header.className = `transcript-card-header ${isUnassigned ? 'is-expander' : ''} ${isUnassigned && isUnassignedExpanded ? 'expanded' : ''}`;
      header.innerHTML = `
        ${isUnassigned ? `
          <svg class="expander-chevron" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="9 18 15 12 9 6"></polyline>
          </svg>
        ` : `
          <svg class="header-icon" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="9 11 12 14 22 4"></polyline><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"></path>
          </svg>
        `}
        <span class="card-title">${title}</span>
        <span class="card-badge tabular-nums">${group.cueIndexes.length} 句</span>
      `;

      card.appendChild(header);

      // Cue rows container
      const list = document.createElement('div');
      list.className = 'cue-list';

      if (isUnassigned) {
        card.setAttribute('data-is-expander', 'true');
        card.setAttribute('data-key', unassignedKey);
        list.classList.add('collapsible');
        if (!isUnassignedExpanded) {
          list.classList.add('collapsed');
        }

        header.addEventListener('click', (e) => {
          e.stopPropagation();
          const isCollapsed = list.classList.toggle('collapsed');
          header.classList.toggle('expanded', !isCollapsed);
          if (!isCollapsed) {
            this.expandedSections.add(unassignedKey);
          } else {
            this.expandedSections.delete(unassignedKey);
          }
        });
      }

      // Distinguish primary vs repeated cues
      const primaryCues = [];
      const repeatedCues = [];
      group.cueIndexes.forEach(idx => {
        if (material && material.repeatedCueIndexes?.includes(idx)) {
          repeatedCues.push(idx);
        } else {
          primaryCues.push(idx);
        }
      });

      primaryCues.forEach(cueIdx => {
        list.appendChild(this.buildCueRowElement(cueIdx, clozeMap[cueIdx], material));
      });

      card.appendChild(list);

      // Repeated Cues Expander
      if (repeatedCues.length > 0) {
        const repeatedKey = 'repeated-' + (material ? material.id : 'm') + '-' + repeatedCues[0];
        const isRepeatedActive = repeatedCues.includes(activeIdx);
        const isRepeatedExpanded = isRepeatedActive || this.expandedSections.has(repeatedKey);

        const repeatedSection = document.createElement('div');
        repeatedSection.className = 'repeated-cues-section';
        repeatedSection.setAttribute('data-repeated-cues', repeatedCues.join(','));
        repeatedSection.setAttribute('data-key', repeatedKey);

        const expanderHeader = document.createElement('div');
        expanderHeader.className = `expander-header ${isRepeatedExpanded ? 'expanded' : ''}`;
        expanderHeader.innerHTML = `
          <svg class="expander-chevron" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="9 18 15 12 9 6"></polyline>
          </svg>
          <span style="flex:1;">重复朗读</span>
          <span class="card-badge tabular-nums">${repeatedCues.length} 句</span>
        `;

        const repeatedContainer = document.createElement('div');
        repeatedContainer.className = 'cue-list repeated-cue-list collapsible';
        if (!isRepeatedExpanded) {
          repeatedContainer.classList.add('collapsed');
        }

        repeatedCues.forEach(cueIdx => {
          repeatedContainer.appendChild(this.buildCueRowElement(cueIdx, clozeMap[cueIdx], material));
        });

        expanderHeader.addEventListener('click', (e) => {
          e.stopPropagation();
          const isCollapsed = repeatedContainer.classList.toggle('collapsed');
          expanderHeader.classList.toggle('expanded', !isCollapsed);
          if (!isCollapsed) {
            this.expandedSections.add(repeatedKey);
          } else {
            this.expandedSections.delete(repeatedKey);
          }
        });

        repeatedSection.appendChild(expanderHeader);
        repeatedSection.appendChild(repeatedContainer);
        card.appendChild(repeatedSection);
      }

      this.dom.transcriptPane.appendChild(card);
    });

    // Re-highlight active cue & sync expanders
    const activeIdxAfter = this.getActiveCueIndex(this.currentTime);
    this.updateActiveCue(this.currentTime, true);
  }

  buildCueRowElement(cueIndex, clozeWordsSet, material) {
    const cue = this.cues[cueIndex];
    const row = document.createElement('div');
    row.className = 'cue-row';
    row.setAttribute('data-cue-index', cueIndex);

    const timeSpan = document.createElement('span');
    timeSpan.className = 'cue-time tabular-nums';
    timeSpan.textContent = SrtParser.formatDuration(cue.start);

    const textSpan = document.createElement('div');
    textSpan.className = 'cue-text';

    // Tokenize text for cloze
    const parts = tokenizeLessonText(cue.text);
    const isMaterialRevealed = material && (
      (this.showAllCloze && !this.hiddenMaterials.has(material.id)) ||
      this.revealedMaterials.has(material.id)
    );

    parts.forEach(part => {
      if (!part.isWord) {
        textSpan.appendChild(document.createTextNode(part.text));
      } else {
        const isCloze = clozeWordsSet && clozeWordsSet.has(part.wordIndex);
        if (!isCloze) {
          textSpan.appendChild(document.createTextNode(part.text));
        } else {
          const isRevealed = this.showAllCloze || isMaterialRevealed || this.revealedCloze.has(`${cueIndex}:${part.wordIndex}`);
          const wordEl = document.createElement('span');
          wordEl.className = `cloze-word ${isRevealed ? 'revealed' : 'hidden'}`;
          wordEl.textContent = part.text;
          wordEl.title = isRevealed ? '点击隐藏' : '点击显示';

          wordEl.addEventListener('click', (e) => {
            e.stopPropagation();
            this.toggleClozeWord(cueIndex, part.wordIndex);
          });

          textSpan.appendChild(wordEl);
        }
      }
    });

    row.appendChild(timeSpan);
    row.appendChild(textSpan);

    row.addEventListener('click', (e) => {
      if (e.target.closest('.cloze-word')) return;
      this.selectCue(cueIndex, e);
    });

    return row;
  }

  selectCue(cueIndex, event = null) {
    if (cueIndex < 0 || cueIndex >= this.cues.length) return;
    const cue = this.cues[cueIndex];

    if (this.isPlaying) {
      // While playing: immediately jump to this cue and keep playing
      this.audio.currentTime = cue.start;
      this.currentTime = cue.start;
      this.updateActiveCue(cue.start, true);
      this.hideCuePopover();
      return;
    }

    // While paused:
    // 1. Remember original paused cue if not yet recorded
    if (this.pausedOriginalCueIndex === null) {
      this.pausedOriginalCueIndex = this.getActiveCueIndex(this.currentTime);
    }

    // 2. Immediately seek audio position to this sentence
    this.audio.currentTime = cue.start;
    this.currentTime = cue.start;

    // 3. Update seek slider & current time text immediately
    const progressRatio = this.duration > 0 ? (cue.start / this.duration) : 0;
    const progressPercent = Math.min(100, Math.max(0, progressRatio * 100));
    const sliderVal = Math.round(progressRatio * 1000);
    const timeStr = SrtParser.formatDuration(cue.start);

    this.dom.seekSlider.value = sliderVal;
    this.dom.seekSlider.style.setProperty('--progress', `${progressPercent}%`);
    this.dom.currentTimeText.textContent = timeStr;

    if (this.dom.miniSeekSlider) {
      this.dom.miniSeekSlider.value = sliderVal;
      this.dom.miniSeekSlider.style.setProperty('--progress', `${progressPercent}%`);
      this.dom.miniCurrentTime.textContent = timeStr;
    }

    // 4. Update active cue row highlighting and active question on the left
    this.updateActiveCue(cue.start, true);

    // 5. Show floating toolbar / popover at the clicked row
    this.showCuePopover(event, cueIndex);

    // 6. Show / update "返回原句" floating badge if we jumped to a different cue
    this.updateReturnOriginalUI(cueIndex);
  }

  showCuePopover(event, cueIndex) {
    this.pendingPopoverCueIndex = cueIndex;
    const popover = this.dom.cuePopover;
    if (!popover || cueIndex < 0 || cueIndex >= this.cues.length) return;

    const cue = this.cues[cueIndex];
    if (this.dom.popoverCueTime) {
      this.dom.popoverCueTime.textContent = SrtParser.formatDuration(cue.start);
    }

    popover.style.display = 'flex';

    // Get position rect from event or cue row
    let rect = null;
    if (event && event.currentTarget && typeof event.currentTarget.getBoundingClientRect === 'function') {
      rect = event.currentTarget.getBoundingClientRect();
    } else {
      const row = document.querySelector(`.cue-row[data-cue-index="${cueIndex}"]`);
      if (row) rect = row.getBoundingClientRect();
    }

    if (!rect) return;

    const popoverWidth = 330;
    const popoverHeight = 44;

    // Center horizontally on the sentence row, constrained by viewport
    let x = rect.left + (rect.width / 2) - (popoverWidth / 2);
    x = Math.max(12, Math.min(window.innerWidth - popoverWidth - 12, x));

    // Place above row if enough room, otherwise below
    let y = rect.top - popoverHeight - 8;
    if (y < 60) {
      y = rect.bottom + 8;
    }
    y = Math.min(window.innerHeight - popoverHeight - 12, Math.max(12, y));

    popover.style.position = 'fixed';
    popover.style.left = `${Math.round(x)}px`;
    popover.style.top = `${Math.round(y)}px`;
  }

  hideCuePopover() {
    if (this.dom.cuePopover) {
      this.dom.cuePopover.style.display = 'none';
    }
    this.pendingPopoverCueIndex = null;
  }

  updateReturnOriginalUI(currentCueIndex) {
    if (this.pausedOriginalCueIndex !== null && this.pausedOriginalCueIndex !== currentCueIndex) {
      const origCue = this.cues[this.pausedOriginalCueIndex];
      const timeStr = origCue ? SrtParser.formatDuration(origCue.start) : '';
      const btn = this.dom.btnReturnOriginal;
      if (btn) {
        btn.innerHTML = `
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="margin-right: 4px;">
            <polyline points="9 14 4 9 9 4"></polyline>
            <path d="M20 20v-7a4 4 0 0 0-4-4H4"></path>
          </svg>
          <span>返回原句 (${timeStr})</span>
        `;
      }
      this.dom.returnOriginalBtnWrap.style.display = 'block';
    } else {
      this.dom.returnOriginalBtnWrap.style.display = 'none';
    }
  }

  // --- Modals ---
  showFileInfoModal() {
    if (!this.currentLesson) return;
    const m = this.currentLesson.manifest;
    const durSec = (m.durationMs ? m.durationMs / 1000 : this.duration);
    document.getElementById('info-title').textContent = m.title || '-';
    document.getElementById('info-uuid').textContent = m.packageUuid || '-';
    document.getElementById('info-duration').textContent = SrtParser.formatDuration(durSec);
    document.getElementById('info-version').textContent = m.packageVersion || 1;
    document.getElementById('info-audio').textContent = m.audioPath || '-';
    document.getElementById('info-transcript').textContent = m.transcriptPath || '-';
    document.getElementById('info-cues-count').textContent = this.cues.length;
    document.getElementById('info-questions-count').textContent = m.exercises?.questions?.length || 0;

    this.dom.fileInfoModal.style.display = 'flex';
  }

  hideFileInfoModal() {
    this.dom.fileInfoModal.style.display = 'none';
  }

  showAlert(title, message) {
    this.dom.alertTitle.textContent = title;
    this.dom.alertMessage.textContent = message;
    this.dom.alertModal.style.display = 'flex';
  }

  // --- Legal Modal & Telemetry ---
  showLegalModal(tab = 'agreement') {
    this.switchLegalTab(tab);
    if (this.dom.legalModal) {
      this.dom.legalModal.style.display = 'flex';
    }
  }

  hideLegalModal() {
    if (this.dom.legalModal) {
      this.dom.legalModal.style.display = 'none';
    }
  }

  switchLegalTab(tabName) {
    document.querySelectorAll('.legal-tab').forEach(tab => {
      tab.classList.toggle('active', tab.getAttribute('data-legal') === tabName);
    });
    if (this.dom.legalContentAgreement) {
      this.dom.legalContentAgreement.style.display = (tabName === 'agreement' ? 'block' : 'none');
    }
    if (this.dom.legalContentPrivacy) {
      this.dom.legalContentPrivacy.style.display = (tabName === 'privacy' ? 'block' : 'none');
    }
    if (this.dom.legalContentAudio) {
      this.dom.legalContentAudio.style.display = (tabName === 'audio' ? 'block' : 'none');
    }
  }

  // --- Global Audio Activation & Unlocking ---
  initGlobalAudioUnlock() {
    const unlockHandler = () => {
      this.unlockAudioSession();
      window.removeEventListener('pointerdown', unlockHandler);
      window.removeEventListener('keydown', unlockHandler);
    };
    window.addEventListener('pointerdown', unlockHandler, { passive: true, once: true });
    window.addEventListener('keydown', unlockHandler, { passive: true, once: true });
  }

  unlockAudioSession() {
    if (this.audioSessionUnlocked) return;
    try {
      // 1. 唤醒并激活 Web Audio Context（解决 iOS/Android 硬件静音通道与 Autoplay 限制）
      const AudioCtxClass = window.AudioContext || window.webkitAudioContext;
      if (AudioCtxClass) {
        if (!this.audioCtx) {
          this.audioCtx = new AudioCtxClass();
        }
        if (this.audioCtx.state === 'suspended') {
          this.audioCtx.resume();
        }
        // 播放极短的静音频脉冲以激活移动端底层硬件音频管道
        const buffer = this.audioCtx.createBuffer(1, 1, 22050);
        const source = this.audioCtx.createBufferSource();
        source.buffer = buffer;
        source.connect(this.audioCtx.destination);
        source.start(0);
      }

      // 2. 预热激活 HTML5 Audio Engine
      if (this.audio) {
        this.audio.volume = 1.0;
        this.audio.muted = false;
        if (!this.audio.src) {
          // 1ms 哑音 wav，避免首次发声被浏览器的自动播放策略静音拦截
          const silentWav = 'data:audio/wav;base64,UklGRigAAABXQVZFZm10IBIAAAABAAEARKwAAIhYAQACABAAAABkYXRhAgAAAAEA';
          this.audio.src = silentWav;
          const p = this.audio.play();
          if (p !== undefined) {
            p.then(() => {
              this.audio.pause();
              this.audio.currentTime = 0;
            }).catch(() => {});
          }
        }
      }

      this.audioSessionUnlocked = true;
      console.log('[Audio] Audio session unlocked and authorized by user gesture');
    } catch (e) {
      console.warn('[Audio] Audio session unlock exception:', e);
    }
  }

  safePlayAudio() {
    this.unlockAudioSession();
    if (!this.audio || !this.audio.src) return;
    this.audio.volume = 1.0;
    this.audio.muted = false;

    const playPromise = this.audio.play();
    if (playPromise !== undefined) {
      playPromise.catch(err => {
        console.warn('[Audio Engine] Play interrupted or blocked:', err);
        if (err.name === 'NotAllowedError') {
          this.showAlert(
            '音频发声受限',
            '检测到浏览器限制了音频发声。请轻触屏幕任意位置或点击下方“好”以完成发声授权；若使用的是 iPhone/iPad，请确认机身左侧物理静音开关已拨回响铃模式（或插上耳机）。'
          );
        }
      });
    }
  }

  updateMediaSession(lesson) {
    if (!('mediaSession' in navigator) || !lesson) return;
    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: lesson.manifest?.title || '精听音频',
        artist: 'Intensive Listening',
        album: '精听训练',
        artwork: [
          { src: 'icons/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: 'icons/icon-512.png', sizes: '512x512', type: 'image/png' }
        ]
      });

      navigator.mediaSession.setActionHandler('play', () => this.togglePlayback());
      navigator.mediaSession.setActionHandler('pause', () => this.togglePlayback());
      navigator.mediaSession.setActionHandler('previoustrack', () => this.stepSentence(-1));
      navigator.mediaSession.setActionHandler('nexttrack', () => this.stepSentence(1));
      navigator.mediaSession.setActionHandler('seekto', (details) => {
        if (details.seekTime !== undefined) {
          this.audio.currentTime = details.seekTime;
          this.currentTime = details.seekTime;
        }
      });
    } catch (e) {
      console.warn('[MediaSession] Setup error:', e);
    }
  }

  reportTelemetryEvent(name, properties = {}) {
    const payload = {
      app_version: '1.0.1+8',
      commit_id: '6d0f5ac',
      ...properties,
      timestamp: Date.now()
    };

    // 1. 阿里云 ARMS RUM
    try {
      const rumInstance = window.RumSDK?.default;
      if (rumInstance && typeof rumInstance.sendCustom === 'function') {
        rumInstance.sendCustom({
          type: 'user_action',
          name,
          properties: payload
        });
      }
    } catch (e) {
      // Silently ignore telemetry failure in offline mode
    }

    // 2. 51.la 网站统计自定义事件
    try {
      if (window.LA && typeof window.LA.track === 'function') {
        window.LA.track(name, payload);
      }
    } catch (e) {
      // Silently ignore
    }
  }

  // --- Ad Blocker Detection & Fullscreen Warning ---
  async checkTelemetryBlocked() {
    // 离线 PWA 模式下直接放行，绝不误判为广告拦截
    if (typeof navigator !== 'undefined' && navigator.onLine === false) {
      return false;
    }

    const endpoint = window.__rum?.endpoint || 
      'https://proj-xtrace-ae8828bae3abd29c8bb93ef647dfd4f1-cn-hangzhou.cn-hangzhou.log.aliyuncs.com/rum/web/v2?workspace=default-cms-1788892690042201-cn-hangzhou&service_id=hm3xyft6jd@5ac84fe55e86125713b2c';

    // 1. 检查 SDK 变量与探针全局对象
    const hasRumSDK = Boolean(window.RumSDK?.default);
    const hasLaSDK = Boolean(window.LA && typeof window.LA.init === 'function');

    // 2. 发起探测请求测试 RUM 遥测通道 (OPTIONS 跨域预检)
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 2200);

    try {
      await fetch(endpoint, {
        method: 'OPTIONS',
        mode: 'cors',
        cache: 'no-store',
        signal: controller.signal
      });
      clearTimeout(timeoutId);

      // 若端点可达，但 SDK 脚本缺失，探测 SDK 脚本自身是否被广告拦截器屏蔽
      if (!hasRumSDK) {
        const sdkController = new AbortController();
        const sdkTimeout = setTimeout(() => sdkController.abort(), 1800);
        try {
          await fetch('https://sdk.rum.aliyuncs.com/v2/browser-sdk.js', {
            method: 'GET',
            cache: 'no-store',
            signal: sdkController.signal
          });
          clearTimeout(sdkTimeout);
        } catch (sdkErr) {
          clearTimeout(sdkTimeout);
          if (typeof navigator === 'undefined' || navigator.onLine) {
            return true; // 探针脚本被拦截
          }
        }
      }

      // 3. 探测 51.la 统计探针是否被拦截
      if (!hasLaSDK) {
        const laController = new AbortController();
        const laTimeout = setTimeout(() => laController.abort(), 1800);
        try {
          await fetch('https://sdk.51.la/js-sdk-pro.min.js', {
            method: 'GET',
            cache: 'no-store',
            signal: laController.signal
          });
          clearTimeout(laTimeout);
        } catch (laErr) {
          clearTimeout(laTimeout);
          if (typeof navigator === 'undefined' || navigator.onLine) {
            return true; // 51.la 脚本被拦截
          }
        }
      }

      return false; // 遥测链路正常
    } catch (err) {
      clearTimeout(timeoutId);
      // 若处于在线状态且请求因 ERR_BLOCKED_BY_CLIENT / Failed to fetch 报错，判定为被拦截
      if (typeof navigator === 'undefined' || navigator.onLine) {
        return true;
      }
      return false;
    }
  }

  ensureTelemetryBeforePlay(playAction) {
    if (typeof playAction !== 'function') return;

    // 1. 同步唤醒激活音频会话管道，确保处于直接用户交互手势 tick 内，杜绝移动端/iOS无声
    this.unlockAudioSession();

    // 2. 若用户已选择忽略、或离线模式，直接同步放行
    if (this.telemetryWarningDismissed || (typeof navigator !== 'undefined' && !navigator.onLine)) {
      playAction();
      return;
    }

    // 3. 若缓存结果已知被广告拦截，弹窗提醒
    if (this.cachedTelemetryBlocked === true) {
      this.pendingPlayAction = playAction;
      this.showAdblockModal();
      return;
    }

    // 4. 若尚未完成背景检测，发起静默检测更新缓存，但不阻塞当前发声
    if (this.cachedTelemetryBlocked === undefined) {
      this.checkTelemetryBlocked().then(isBlocked => {
        this.cachedTelemetryBlocked = isBlocked;
      }).catch(() => {
        this.cachedTelemetryBlocked = false;
      });
    }

    // 同步执行播放，确保 100% 保持浏览器用户交互手势授权
    playAction();
  }

  showAdblockModal() {
    if (this.dom.adblockOverlay) {
      this.dom.adblockOverlay.style.display = 'flex';
      if (this.dom.adblockRetryMsg) {
        this.dom.adblockRetryMsg.style.display = 'none';
        this.dom.adblockRetryMsg.textContent = '';
      }
    }
  }

  hideAdblockModal() {
    if (this.dom.adblockOverlay) {
      this.dom.adblockOverlay.style.display = 'none';
    }
  }

  async handleAdblockRetry() {
    if (!this.dom.btnAdblockRetry) return;
    this.dom.btnAdblockRetry.disabled = true;
    if (this.dom.adblockRetryMsg) {
      this.dom.adblockRetryMsg.style.display = 'block';
      this.dom.adblockRetryMsg.style.color = 'var(--text-secondary)';
      this.dom.adblockRetryMsg.textContent = '正在重新检测遥测通道，请稍候...';
    }

    const isBlocked = await this.checkTelemetryBlocked();
    this.dom.btnAdblockRetry.disabled = false;

    if (!isBlocked) {
      if (this.dom.adblockRetryMsg) {
        this.dom.adblockRetryMsg.style.color = '#107c10';
        this.dom.adblockRetryMsg.textContent = '检测通过！正在开启播放...';
      }
      setTimeout(() => {
        this.hideAdblockModal();
        const action = this.pendingPlayAction;
        this.pendingPlayAction = null;
        if (typeof action === 'function') {
          action();
        }
      }, 400);
    } else {
      if (this.dom.adblockRetryMsg) {
        this.dom.adblockRetryMsg.style.color = 'var(--accent-red)';
        this.dom.adblockRetryMsg.textContent = '检测到遥测通道仍被拦截。请确认已关闭拦截插件（如 uBlock Origin / AdGuard 等）或将本站加入白名单后重试。';
      }
    }
  }

  handleAdblockIgnore() {
    this.telemetryWarningDismissed = true;
    this.hideAdblockModal();
    const action = this.pendingPlayAction;
    this.pendingPlayAction = null;
    if (typeof action === 'function') {
      action();
    }
  }

  getTelemetryStatus() {
    const hasSDK = Boolean(window.RumSDK?.default);
    const isConsentAgreed = localStorage.getItem('il_privacy_consent') === 'true';
    const anonUid = localStorage.getItem('il_telemetry_uid');
    const endpoint = window.__rum?.endpoint;
    return {
      status: hasSDK ? 'working' : 'waiting_or_blocked',
      sdkLoaded: Boolean(window.RumSDK),
      sdkInitialized: hasSDK,
      consentAgreed: isConsentAgreed,
      anonUid: anonUid,
      endpoint: endpoint,
      isOnline: typeof navigator !== 'undefined' ? navigator.onLine : true,
      telemetryWarningDismissed: this.telemetryWarningDismissed
    };
  }

  initTelemetryAndLegal() {
    // 1. Fullscreen Blocking Legal Overlay on First Visit (全屏阻断式协议弹窗)
    const agreed = localStorage.getItem('il_privacy_consent');
    if (!agreed && this.dom.blockingLegalOverlay) {
      this.dom.blockingLegalOverlay.style.display = 'flex';
    }

    // 2. Client ID resolution
    let anonUid = localStorage.getItem('il_telemetry_uid');
    if (!anonUid) {
      anonUid = 'u_' + Math.random().toString(36).substring(2, 9) + Date.now().toString(36);
      try {
        localStorage.setItem('il_telemetry_uid', anonUid);
      } catch (e) {}
    }

    // 3. Collect Basic Client Environment Info
    const basicInfo = {
      uid: anonUid,
      screenResolution: `${window.screen?.width || 0}x${window.screen?.height || 0}`,
      viewport: `${window.innerWidth}x${window.innerHeight}`,
      pixelRatio: window.devicePixelRatio || 1,
      language: navigator.language || 'zh-CN',
      platform: navigator.platform || 'web',
      userAgent: navigator.userAgent || '',
      referrer: document.referrer || 'direct',
      timezone: (typeof Intl !== 'undefined' && Intl.DateTimeFormat) ? Intl.DateTimeFormat().resolvedOptions().timeZone : 'unknown',
      themeMode: localStorage.getItem('theme_mode') || 'auto',
      pageUrl: window.location.href,
      openTime: new Date().toISOString()
    };

    // 4. Report Basic Info via Alibaba Cloud ARMS RUM
    const sendLaunchTelemetry = () => {
      const rumInstance = window.RumSDK?.default;
      if (!rumInstance) return false;

      try {
        if (typeof rumInstance.setConfig === 'function') {
          rumInstance.setConfig({
            user: { id: anonUid }
          });
        }
        if (typeof rumInstance.sendCustom === 'function') {
          rumInstance.sendCustom({
            type: 'page_open',
            name: 'page_open_basic_info',
            properties: basicInfo
          });
        }
        return true;
      } catch (err) {
        console.warn('[RUM] Launch telemetry report failed:', err);
        return true;
      }
    };

    // Attempt immediately, or retry until SDK finishes loading (max 3 seconds)
    if (!sendLaunchTelemetry()) {
      let attempts = 0;
      const maxAttempts = 15;
      const interval = setInterval(() => {
        attempts++;
        if (sendLaunchTelemetry() || attempts >= maxAttempts) {
          clearInterval(interval);
        }
      }, 200);
    }

    // 5. Background pre-check for telemetry channel (silent, non-blocking)
    this.checkTelemetryBlocked().then(isBlocked => {
      this.cachedTelemetryBlocked = isBlocked;
    }).catch(() => {
      this.cachedTelemetryBlocked = false;
    });
  }

  initPwaServiceWorker() {
    // 1. Register Service Worker for Offline PWA
    if ('serviceWorker' in navigator) {
      const registerSW = () => {
        navigator.serviceWorker.register('./sw.js')
          .then((reg) => {
            console.log('[PWA] Service Worker registered:', reg.scope);
            reg.addEventListener('updatefound', () => {
              const newWorker = reg.installing;
              newWorker?.addEventListener('statechange', () => {
                if (newWorker.state === 'installed' && navigator.serviceWorker.controller) {
                  console.log('[PWA] App cache updated. Ready for offline use.');
                }
              });
            });
          })
          .catch((err) => {
            console.warn('[PWA] SW register error:', err);
          });
      };

      if (document.readyState === 'complete' || document.readyState === 'interactive') {
        registerSW();
      } else {
        window.addEventListener('load', registerSW);
      }
    }

    // 2. PWA Install Prompt (Add to Home Screen / Desktop Client)
    let deferredPrompt = null;
    window.addEventListener('beforeinstallprompt', (e) => {
      e.preventDefault();
      deferredPrompt = e;
      if (this.dom.btnInstallPwa) {
        this.dom.btnInstallPwa.style.display = 'inline-flex';
      }
    });

    if (this.dom.btnInstallPwa) {
      this.dom.btnInstallPwa.addEventListener('click', async () => {
        if (deferredPrompt) {
          deferredPrompt.prompt();
          const choice = await deferredPrompt.userChoice;
          console.log('[PWA] Install prompt outcome:', choice.outcome);
          deferredPrompt = null;
          this.dom.btnInstallPwa.style.display = 'none';
        } else {
          this.showAlert(
            '安装本地客户端',
            '本应用已具备渐进式 Web 应用 (PWA) 离线能力。您可通过浏览器地址栏右侧图标或浏览器菜单中的“安装应用 / 添加到主屏幕”，将本播放器安装为原生应用，安装后断网亦可全功能离线使用。'
          );
        }
      });
    }

    window.addEventListener('appinstalled', () => {
      console.log('[PWA] App installed successfully');
      if (this.dom.btnInstallPwa) {
        this.dom.btnInstallPwa.style.display = 'none';
      }
    });
  }

  escapeHtml(text = '') {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }
}

// Instantiate on DOM ready
document.addEventListener('DOMContentLoaded', () => {
  window.__intensiveListeningApp = new IntensiveListeningApp();
});
