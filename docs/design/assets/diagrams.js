(() => {
  document.documentElement.classList.add('js');

  const motionToggle = document.getElementById('animation-toggle');
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  let motionPaused = true;

  function setMotionPaused(value) {
    motionPaused = value;
    motionToggle.textContent = value ? 'Play motion' : 'Pause motion';
    motionToggle.setAttribute('aria-pressed', String(!value));
    document.querySelectorAll('svg').forEach(svg => {
      if (value) svg.pauseAnimations();
      else svg.unpauseAnimations();
    });
  }

  motionToggle.addEventListener('click', () => setMotionPaused(!motionPaused));
  reducedMotion.addEventListener('change', event => {
    if (event.matches) setMotionPaused(true);
  });
  setMotionPaused(true);

  const sidebar = document.querySelector('.sidebar');
  const navigationToggle = document.getElementById('nav-toggle');

  function setNavigationOpen(value) {
    sidebar.classList.toggle('is-open', value);
    navigationToggle.setAttribute('aria-expanded', String(value));
  }

  navigationToggle.addEventListener('click', () => {
    setNavigationOpen(navigationToggle.getAttribute('aria-expanded') !== 'true');
  });
  document.querySelectorAll('#page-navigation a').forEach(link => {
    link.addEventListener('click', () => setNavigationOpen(false));
  });
  sidebar.addEventListener('keydown', event => {
    if (event.key === 'Escape' && navigationToggle.getAttribute('aria-expanded') === 'true') {
      setNavigationOpen(false);
      navigationToggle.focus();
    }
  });

  const sectionLinks = Array.from(document.querySelectorAll('nav a'));
  const sectionHeadings = Array.from(document.querySelectorAll('article h2'));

  function updateCurrentSection() {
    let current = '';
    const boundary = Math.max(160, window.innerHeight * 0.2);
    for (const heading of sectionHeadings) {
      if (heading.getBoundingClientRect().top > boundary) break;
      current = heading.id;
    }
    sectionLinks.forEach(link => {
      if (link.hash === '#' + current) link.setAttribute('aria-current', 'location');
      else link.removeAttribute('aria-current');
    });
  }

  window.addEventListener('scroll', updateCurrentSection, { passive: true });
  window.addEventListener('resize', updateCurrentSection);
  updateCurrentSection();

  document.querySelectorAll('[data-memory]').forEach(button => {
    button.addEventListener('click', () => {
      const moved = button.dataset.memory === 'after';
      document.getElementById('memory-address').textContent = moved
        ? 'List → segment 9 : offset 64'
        : 'List → segment 7 : offset 128';
      document.getElementById('memory-route').setAttribute('d', moved
        ? 'M200 189 V220 H297 V284'
        : 'M200 189 V220 H103 V284');
      document.getElementById('memory-old-slot').setAttribute('class', moved ? 'slot vacant' : 'slot live');
      document.getElementById('memory-new-slot').setAttribute('class', moved ? 'slot live' : 'slot vacant');
      document.getElementById('memory-old-label').textContent = moved ? 'Free slot' : 'List payload';
      document.getElementById('memory-new-label').textContent = moved ? 'List payload' : 'Free slot';
      document.getElementById('memory-description').textContent = moved
        ? 'Compaction copies this block under owner protection and repairs its descriptor. After active users are safe, the old slot is reusable. The entity key, root entry and logical revision stay unchanged.'
        : 'The root owns this List block. Its descriptor stores a relative address; the primary key and stable root entry do not change when the block moves.';
      document.querySelectorAll('[data-memory]').forEach(peer => {
        peer.setAttribute('aria-pressed', String(peer === button));
      });
    });
  });

  const stages = [
    {
      title: 'Connect',
      description: 'An empty child connects to its configured master. Application reads are not admitted yet.',
      master: 'Connect to the configured master',
      wire: 'Connect'
    },
    {
      title: 'Fence the capture at S',
      description: 'The master fixes the root-catalog scan range and retains the needed delivery tail. Deletions keep temporary identity and revision metadata.',
      master: 'Capture starts at S',
      wire: 'Schema + S'
    },
    {
      title: 'Copy coherent roots',
      description: 'Each root arrives with its own watermark. The provisional arena may contain different positions, so it stays unreadable.',
      master: 'Root copies carry watermarks',
      wire: 'Binary blocks'
    },
    {
      title: 'Catch up through R',
      description: 'After the scan fixes R, finish installation, build local indexes and replay complete transactions through R. Per-root watermarks avoid duplicate effects; WriteBatch stays whole.',
      master: 'Scan complete · fixed boundary R',
      wire: 'Tail through R'
    },
    {
      title: 'Ready at R',
      description: 'The complete state and local indexes are ready through R. Open reads and acknowledge that applied boundary together.',
      master: 'Required capture state can be released',
      wire: 'Applied ACK'
    },
    {
      title: 'Apply live changes',
      description: 'The master pushes confirmed logical effects. The child prepares and publishes them with indexes, preserving transaction boundaries and advancing its applied position.',
      master: 'Continue the ordered live stream',
      wire: 'Changes'
    }
  ];
  let stage = 0;

  function renderStage() {
    const value = stages[stage];
    document.getElementById('replication-step').textContent = 'Stage ' + (stage + 1) + ' of ' + stages.length;
    document.getElementById('replication-stage-title').textContent = value.title;
    document.getElementById('replication-description').textContent = value.description;
    document.getElementById('capture-label').textContent = value.master;
    document.getElementById('wire-label').textContent = value.wire;
    for (let block = 1; block <= 4; block += 1) {
      const installed = stage >= 3 || (stage === 2 && block <= 2);
      document.getElementById('page-' + block).setAttribute('class', installed ? 'slot ready' : 'slot');
    }
    document.getElementById('request-box').setAttribute('class', stage >= 4 ? 'node core' : 'node pending');
    document.getElementById('request-label').textContent = stage >= 4 ? 'Ready · reads open' : 'Not ready · reads closed';
    document.getElementById('replication-next').disabled = stage === stages.length - 1;
  }

  document.getElementById('replication-next').addEventListener('click', () => {
    if (stage < stages.length - 1) stage += 1;
    renderStage();
  });
  document.getElementById('replication-reset').addEventListener('click', () => {
    stage = 0;
    renderStage();
  });
  renderStage();

  const requiredPosition = 120;
  let appliedPosition = 117;
  const advance = document.getElementById('session-advance');

  function renderPosition() {
    const ready = appliedPosition >= requiredPosition;
    const result = document.getElementById('session-result');
    document.getElementById('session-applied').textContent = String(appliedPosition);
    result.classList.toggle('is-ready', ready);
    if (!ready) result.textContent = 'Waiting: ' + appliedPosition + ' is below the required position 120.';
    else if (appliedPosition === requiredPosition) result.textContent = 'Read admitted: position 120 is applied. No other replica is awaited.';
    else result.textContent = 'Read still admitted at 121. The dependency is satisfied; later changes remain visible.';
    advance.textContent = appliedPosition < requiredPosition ? 'Apply next change' : 'Apply a later change';
    advance.disabled = appliedPosition > requiredPosition;
  }

  advance.addEventListener('click', () => {
    if (appliedPosition <= requiredPosition) appliedPosition += 1;
    renderPosition();
  });
  document.getElementById('session-reset').addEventListener('click', () => {
    appliedPosition = 117;
    renderPosition();
  });
  renderPosition();
})();
