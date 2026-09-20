import { Directory, File, Paths } from 'expo-file-system';

export type MobileAppConfig = {
  serverUrl: string;
  retainLog: number;
};

const DEFAULT_RETAIN_LOG_DAYS = 3;
const CONFIG_FILE_NAME = 'config.json';
const LOG_DIRECTORY_NAME = 'logs';

let activeConfig: MobileAppConfig | null = null;
let configuredRetainLog: number | null = null;
let logQueue: Promise<void> = Promise.resolve();

function getConfigFile(): File {
  return new File(Paths.document, CONFIG_FILE_NAME);
}

function getLogDirectory(): Directory {
  return new Directory(Paths.document, LOG_DIRECTORY_NAME);
}

function getLogFile(dateKey: string): File {
  return new File(getLogDirectory(), `${dateKey}.log`);
}

function writeFile(file: File, content: string): void {
  if (!file.exists) {
    file.create({ intermediates: true });
  }

  file.write(content);
}

function normalizeRetainLog(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return DEFAULT_RETAIN_LOG_DAYS;
  }

  return Math.min(3, Math.max(0, Math.floor(value)));
}

function parseConfig(value: string, defaultServerUrl: string): MobileAppConfig {
  const parsed = JSON.parse(value) as { serverUrl?: unknown; retainLog?: unknown };

  return {
    serverUrl:
      typeof parsed.serverUrl === 'string' && parsed.serverUrl.trim().length > 0
        ? parsed.serverUrl.trim()
        : defaultServerUrl,
    retainLog: normalizeRetainLog(parsed.retainLog)
  };
}

function getDateKey(date: Date): string {
  const year = date.getFullYear().toString().padStart(4, '0');
  const month = (date.getMonth() + 1).toString().padStart(2, '0');
  const day = date.getDate().toString().padStart(2, '0');
  return `${year}-${month}-${day}`;
}

function getRetainedDateKeys(retainLog: number): Set<string> {
  const retainedDates = new Set<string>();
  const date = new Date();
  date.setHours(12, 0, 0, 0);

  for (let offset = 0; offset < retainLog; offset += 1) {
    retainedDates.add(getDateKey(date));
    date.setDate(date.getDate() - 1);
  }

  return retainedDates;
}

async function pruneLogFiles(retainLog: number): Promise<void> {
  let entries: (File | Directory)[];
  try {
    entries = getLogDirectory().list();
  } catch {
    return;
  }

  const retainedDates = getRetainedDateKeys(retainLog);
  entries
    .filter(entry => entry.name.endsWith('.log'))
    .filter(entry => !retainedDates.has(entry.name.slice(0, -4)))
    .forEach(entry => {
      try {
        entry.delete();
      } catch {
        // A stale log is best-effort cleanup.
      }
    });
}

async function readConfiguredRetainLog(): Promise<number> {
  if (configuredRetainLog !== null) {
    return configuredRetainLog;
  }

  try {
    const value = await getConfigFile().text();
    const parsed = JSON.parse(value) as { retainLog?: unknown };
    configuredRetainLog = normalizeRetainLog(parsed.retainLog);
  } catch {
    configuredRetainLog = DEFAULT_RETAIN_LOG_DAYS;
  }

  return configuredRetainLog;
}

export async function loadAppConfig(defaultServerUrl: string): Promise<MobileAppConfig> {
  if (activeConfig) {
    return activeConfig;
  }

  let config: MobileAppConfig;
  let shouldCreateConfig = false;
  try {
    const value = await getConfigFile().text();
    config = parseConfig(value, defaultServerUrl);
  } catch {
    config = {
      serverUrl: defaultServerUrl,
      retainLog: DEFAULT_RETAIN_LOG_DAYS
    };
    shouldCreateConfig = true;
  }

  activeConfig = config;
  configuredRetainLog = config.retainLog;

  if (shouldCreateConfig) {
    try {
      writeFile(getConfigFile(), `${JSON.stringify(config, null, 2)}\n`);
    } catch {
      // The in-memory defaults still let the app start when storage is unavailable.
    }
  }

  await pruneLogFiles(config.retainLog);
  return config;
}

export async function appendAppLog(message: string, details?: Record<string, unknown>): Promise<void> {
  const operation = logQueue.then(async () => {
    const retainLog = await readConfiguredRetainLog();
    if (retainLog === 0) {
      return;
    }

    const logDirectory = getLogDirectory();
    logDirectory.create({ intermediates: true, idempotent: true });

    const logFile = getLogFile(getDateKey(new Date()));
    let existingContent = '';
    try {
      existingContent = await logFile.text();
    } catch {
      // This is the first entry for the current date.
    }

    let detailText = '';
    if (details) {
      try {
        detailText = ` ${JSON.stringify(details)}`;
      } catch {
        detailText = '';
      }
    }

    const line = `${new Date().toISOString()} ${message}${detailText}\n`;
    writeFile(logFile, `${existingContent}${line}`);
    await pruneLogFiles(retainLog);
  });

  logQueue = operation.catch(() => undefined);
  await operation.catch(() => undefined);
}