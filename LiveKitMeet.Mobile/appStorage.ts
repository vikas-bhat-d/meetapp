import { Directory, File, Paths } from 'expo-file-system';
import { Platform } from 'react-native';

export type MobileAppConfig = {
  serverUrl: string;
  retainLog: number;
};

const DEFAULT_RETAIN_LOG_DAYS = 3;
const APP_DATA_DIRECTORY_NAME = 'wincalldata';
const LEGACY_APP_DATA_DIRECTORY_NAME = '.wincalldata';
const CONFIG_FILE_NAME = 'config.json';
const LOG_DIRECTORY_NAME = 'logs';
const LOG_FILE_MIME_TYPE = 'application/octet-stream';
const STORAGE_LOCATION_FILE_NAME = 'storage-location.json';

let activeConfig: MobileAppConfig | null = null;
let activeDataDirectory: Directory | null = null;
let configuredRetainLog: number | null = null;
let logQueue: Promise<void> = Promise.resolve();

function getStorageLocationFile(): File {
  return new File(Paths.document, STORAGE_LOCATION_FILE_NAME);
}

function getDataDirectory(): Directory {
  return activeDataDirectory ?? Paths.document;
}

function getConfigFile(): File {
  return findFileChild(getDataDirectory(), CONFIG_FILE_NAME) ?? new File(getDataDirectory(), CONFIG_FILE_NAME);
}

function getLogDirectory(): Directory {
  return findDirectoryChild(getDataDirectory(), LOG_DIRECTORY_NAME) ?? new Directory(getDataDirectory(), LOG_DIRECTORY_NAME);
}

function getLogFile(dateKey: string): File {
  const logDirectory = getLogDirectory();
  return findFileChild(logDirectory, `${dateKey}.log`) ?? new File(logDirectory, `${dateKey}.log`);
}

function writeFile(file: File, content: string): void {
  if (!file.exists) {
    file.create({ intermediates: true });
  }

  file.write(content);
}

function findFileChild(directory: Directory, fileName: string): File | null {
  try {
    const entry = directory.list().find(item => item instanceof File && item.name === fileName);
    return entry instanceof File ? entry : null;
  } catch {
    return null;
  }
}

function findDirectoryChild(directory: Directory, directoryName: string): Directory | null {
  try {
    const entry = directory.list().find(item => item instanceof Directory && item.name === directoryName);
    return entry instanceof Directory ? entry : null;
  } catch {
    return null;
  }
}

function createDirectory(parent: Directory, directoryName: string): Directory {
  const existing = findDirectoryChild(parent, directoryName);
  if (existing) {
    return existing;
  }

  try {
    return parent.createDirectory(directoryName);
  } catch {
    const directory = new Directory(parent, directoryName);
    directory.create({ idempotent: true });
    return directory;
  }
}

function createFile(parent: Directory, fileName: string, mimeType: string): File {
  const existing = findFileChild(parent, fileName);
  if (existing) {
    return existing;
  }

  try {
    return parent.createFile(fileName, mimeType);
  } catch {
    const file = new File(parent, fileName);
    file.create({ intermediates: true });
    return file;
  }
}

function createConfigFile(): File {
  return createFile(getDataDirectory(), CONFIG_FILE_NAME, 'application/json');
}

function createLogDirectory(): Directory {
  return createDirectory(getDataDirectory(), LOG_DIRECTORY_NAME);
}

function createLogFile(dateKey: string): File {
  return createFile(createLogDirectory(), `${dateKey}.log`, LOG_FILE_MIME_TYPE);
}

async function readStoredDataDirectory(): Promise<Directory | null> {
  try {
    const value = await getStorageLocationFile().text();
    const parsed = JSON.parse(value) as { directoryUri?: unknown };
    if (typeof parsed.directoryUri !== 'string' || parsed.directoryUri.trim().length === 0) {
      return null;
    }

    const directory = new Directory(parsed.directoryUri);
    return directory.exists ? directory : null;
  } catch {
    return null;
  }
}

function rememberDataDirectory(directory: Directory): void {
  try {
    writeFile(
      getStorageLocationFile(),
      `${JSON.stringify({ directoryUri: directory.uri }, null, 2)}\n`
    );
  } catch {
    // The app can use the selected directory for this run even if the pointer cannot be saved.
  }
}

async function migrateLegacyDataDirectory(directory: Directory): Promise<Directory> {
  if (directory.name !== LEGACY_APP_DATA_DIRECTORY_NAME) {
    return directory;
  }

  try {
    const visibleDirectory = createDirectory(directory.parentDirectory, APP_DATA_DIRECTORY_NAME);

    const legacyConfig = findFileChild(directory, CONFIG_FILE_NAME);
    const visibleConfig = findFileChild(visibleDirectory, CONFIG_FILE_NAME);
    if (legacyConfig && !visibleConfig) {
      writeFile(createFile(visibleDirectory, CONFIG_FILE_NAME, 'application/json'), await legacyConfig.text());
    }

    const legacyLogs = findDirectoryChild(directory, LOG_DIRECTORY_NAME);
    if (legacyLogs) {
      const visibleLogs = createDirectory(visibleDirectory, LOG_DIRECTORY_NAME);
      for (const entry of legacyLogs.list()) {
        if (!(entry instanceof File) || !entry.name.endsWith('.log')) {
          continue;
        }

        if (!findFileChild(visibleLogs, entry.name)) {
          writeFile(createFile(visibleLogs, entry.name, LOG_FILE_MIME_TYPE), await entry.text());
        }
      }
    }

    rememberDataDirectory(visibleDirectory);
    return visibleDirectory;
  } catch {
    return directory;
  }
}

async function resolveDataDirectory(promptForPermission: boolean): Promise<Directory> {
  if (activeDataDirectory) {
    return activeDataDirectory;
  }

  const storedDirectory = await readStoredDataDirectory();
  if (storedDirectory) {
    activeDataDirectory = await migrateLegacyDataDirectory(storedDirectory);
    return activeDataDirectory;
  }

  if (Platform.OS === 'android' && promptForPermission) {
    try {
      const selectedDirectory = new Directory((await Directory.pickDirectoryAsync()).uri);
      let dataDirectory: Directory;
      if (selectedDirectory.name === LEGACY_APP_DATA_DIRECTORY_NAME) {
        dataDirectory = await migrateLegacyDataDirectory(selectedDirectory);
      } else if (selectedDirectory.name === APP_DATA_DIRECTORY_NAME) {
        dataDirectory = selectedDirectory;
      } else {
        try {
          dataDirectory = createDirectory(selectedDirectory, APP_DATA_DIRECTORY_NAME);
        } catch {
          // Some document providers allow files in the selected folder but reject child folders.
          dataDirectory = selectedDirectory;
        }
      }

      activeDataDirectory = dataDirectory;
      rememberDataDirectory(dataDirectory);
      return dataDirectory;
    } catch {
      // Use app-private storage when the user cancels or the provider rejects the folder.
    }
  }

  activeDataDirectory = Paths.document;
  return activeDataDirectory;
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

  await resolveDataDirectory(false);

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

  await resolveDataDirectory(true);

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
      writeFile(createConfigFile(), `${JSON.stringify(config, null, 2)}\n`);
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

    const logFile = createLogFile(getDateKey(new Date()));
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