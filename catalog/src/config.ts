export interface WebsiteConfig {
  title: string;
  footer: string;
  short_name: string;
  display_name: string;
  description: string;
  default_language: string;
  languages: Record<string, string>;
  backend: string;
  backend_timeout?: number;
  delay_total_count?: boolean;
  enable_websocket?: boolean;
  enable_messaging?: boolean;
  enable_surveys?: boolean;
  enable_poll?: boolean;
  enable_reaction_and_comment?: boolean;
  enable_public_view?: boolean;
  enable_notifications?: boolean;
  use_admin_space_view?: boolean;
  theme?: {
    type: "solid" | "gradient";
    value: string;
  };
}

const defaultConfig: WebsiteConfig = {
  title: "DMART Unified Data Platform",
  footer: "dmart.cc unified data platform",
  short_name: "dmart",
  display_name: "dmart",
  description: "dmart unified data platform",
  default_language: "ar",
  languages: { ar: "العربية", en: "English" },
  backend: "http://localhost:8282/dmart",
  backend_timeout: 30000,
  delay_total_count: false,
  enable_websocket: true,
  enable_messaging: false,
  enable_surveys: false,
  enable_poll: false,
  enable_reaction_and_comment: false,
  enable_public_view: false,
  enable_notifications: false,
  use_admin_space_view: false,
};

const loadConfig = async (): Promise<WebsiteConfig> => {
  try {
    const configUrl = new URL("config.json", document.baseURI).href;
    const response = await fetch(configUrl);
    if (!response.ok) {
      throw new Error(
        `Failed to load config: ${response.status} ${response.statusText}`,
      );
    }
    return await response.json();
  } catch (error) {
    console.error("Error loading configuration:", error);
    if (import.meta.env.PROD) {
      console.error(
        "CRITICAL: config.json could not be loaded in production. Using default config.",
      );
    }
    return { ...defaultConfig };
  }
};

export let website: WebsiteConfig = { ...defaultConfig };

export const configReady: Promise<void> = loadConfig().then((config) => {
  website = config;
});
