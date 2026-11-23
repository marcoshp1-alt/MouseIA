using System;
using System.Collections.Generic;
using System.Globalization;

namespace MouseIA;

/// <summary>Interface text in Portuguese (Brazil) and English.</summary>
public static class Loc
{
    private static bool _portuguese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";

    public static bool IsPortuguese => _portuguese;

    /// <summary>The culture answers are written in: the Windows language, or the chosen interface language.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    public static void Apply(string setting)
    {
        var windows = CultureInfo.CurrentUICulture;
        _portuguese = setting switch
        {
            "pt" => true,
            "en" => false,
            _ => windows.TwoLetterISOLanguageName == "pt",
        };
        Culture = setting switch
        {
            "pt" => windows.TwoLetterISOLanguageName == "pt" ? windows : new CultureInfo("pt-BR"),
            "en" => windows.TwoLetterISOLanguageName == "en" ? windows : new CultureInfo("en-US"),
            _ => windows,
        };
    }

    public static string T(string key) =>
        Strings.TryGetValue(key, out var pair) ? (_portuguese ? pair.Pt : pair.En) : key;

    public static string Format(string key, params object[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(key), args); }
        catch (FormatException) { return T(key); }
    }

    public static IEnumerable<string> Keys => Strings.Keys;

    private static readonly Dictionary<string, (string Pt, string En)> Strings = new()
    {
        // Overlay
        ["hint"] = ("Clique numa janela ou arraste para selecionar · Esc sai", "Click a window or drag to select · Esc exits"),
        ["hint_typing"] = ("Clique numa janela, arraste para selecionar ou comece a digitar · Esc sai", "Click a window, drag to select or just start typing · Esc exits"),
        ["selection"] = ("Seleção", "Selection"),

        // Card
        ["add_selection"] = ("Adicionar outra seleção", "Add another selection"),
        ["settings"] = ("Configurações", "Settings"),
        ["close"] = ("Fechar", "Close"),
        ["close_esc"] = ("Fechar (Esc)", "Close (Esc)"),
        ["remove"] = ("Remover", "Remove"),
        ["translate"] = ("Traduzir", "Translate"),
        ["translate_tip"] = ("Traduz o texto da seleção para o idioma escolhido", "Translates the text in the selection into the chosen language"),
        ["translate_to"] = ("Traduzir para", "Translate to"),
        ["translate_to_x"] = ("Traduzir para {0}", "Translate into {0}"),
        ["copy_text"] = ("Copiar texto", "Copy text"),
        ["copy_text_tip"] = ("Reconhece o texto da imagem (offline) e copia", "Recognizes the text in the image (offline) and copies it"),
        ["qr_code"] = ("QR Code", "QR code"),
        ["qr_tip"] = ("Lê QR Codes e códigos de barras da seleção", "Reads QR codes and barcodes in the selection"),
        ["voice_translator"] = ("Tradutor de voz", "Voice translator"),
        ["voice_tip"] = ("Fale num idioma e ouça em outro", "Speak in one language, hear it in another"),
        ["open_file"] = ("Arquivo", "File"),
        ["open_file_tip"] = ("Imagens, PDF, vídeos, Word, PowerPoint, textos… (ou arraste para cá)", "Images, PDF, videos, Word, PowerPoint, text… (or drop them here)"),
        ["prompt_hint"] = ("Pergunte ou peça algo…", "Ask or request anything…"),
        ["dictate"] = ("Ditar por voz", "Dictate"),
        ["send"] = ("Enviar (Enter)", "Send (Enter)"),
        ["stop"] = ("Parar", "Stop"),
        ["listening"] = ("Ouvindo… fale agora", "Listening… speak now"),
        ["reading"] = ("Lendo…", "Reading…"),
        ["opening"] = ("Abrindo…", "Opening…"),
        ["copy"] = ("Copiar", "Copy"),
        ["listen"] = ("Ouvir", "Listen"),
        ["retry"] = ("Tentar de novo", "Try again"),
        ["details"] = ("Detalhes", "Details"),
        ["open_link"] = ("Abrir link", "Open link"),
        ["copy_password"] = ("Copiar senha", "Copy password"),
        ["stopped"] = ("Interrompido.", "Stopped."),
        ["answer_truncated"] = ("A resposta atingiu o limite de tamanho.", "The answer reached its length limit."),
        ["used_fallback"] = ("Respondido por {0}, porque o modelo escolhido não estava disponível.", "Answered by {0} because the chosen model wasn't available."),
        ["text_copied"] = ("Texto copiado", "Text copied"),
        ["no_text_found"] = ("Nenhum texto encontrado na seleção.", "No text found in the selection."),
        ["ocr_missing"] = ("O Windows não tem um idioma de reconhecimento de texto instalado. Adicione um idioma em Configurações > Hora e idioma.", "Windows has no text recognition language installed. Add a language in Settings > Time & language."),
        ["code_found"] = ("Código encontrado: {0}", "Code found: {0}"),
        ["code_kind"] = ("Código {0}", "{0} code"),
        ["no_code_found"] = ("Nenhum QR Code encontrado.", "No QR code found."),
        ["no_code_hint"] = ("Dica: selecione o código mais de perto, com um pouco de margem em volta.", "Tip: select the code more closely, with a little margin around it."),
        ["wifi_network"] = ("Rede Wi-Fi: {0}", "Wi-Fi network: {0}"),
        ["wifi_password"] = ("Senha: {0}", "Password: {0}"),
        ["file_unsupported"] = ("Ainda não consigo abrir \"{0}\".", "Can't open \"{0}\" yet."),
        ["file_failed"] = ("Não foi possível abrir \"{0}\".", "Couldn't open \"{0}\"."),
        ["pdf_pages"] = ("Do PDF, entraram as primeiras páginas ({0}).", "The first pages of the PDF were added ({0})."),
        ["video_frames"] = ("Do vídeo {0} ({1}), entraram 3 quadros: início, meio e fim. Para um momento exato, pause o vídeo e selecione na tela.", "3 frames of {0} ({1}) were added: start, middle and end. For an exact moment, pause the video and select it on screen."),
        ["needs_ai"] = ("Para conversar, configure uma IA: uma chave grátis do Gemini ou uma IA local (Ollama, LM Studio). Copiar texto e ler QR Code já funcionam sem IA.", "To chat, set up an AI: a free Gemini key or a local AI (Ollama, LM Studio). Copy text and QR codes already work without one."),
        ["needs_ai_voice"] = ("Sem uma IA configurada, a tradução abre no Google Tradutor.", "Without an AI set up, the translation opens in Google Translate."),
        ["configure_ai"] = ("Configurar IA", "Set up AI"),
        ["check_settings"] = ("Confira a chave e o provedor nas configurações.", "Check the key and provider in settings."),
        ["open_settings"] = ("Abrir configurações", "Open settings"),
        ["local_ai"] = ("IA local", "Local AI"),

        // Chips
        ["chip_what"] = ("O que é isto?", "What is this?"),
        ["chip_what_prompt"] = ("O que é isto? Explique de forma breve.", "What is this? Explain briefly."),
        ["chip_explain"] = ("Explique", "Explain"),
        ["chip_explain_prompt"] = ("Explique isto de um jeito simples.", "Explain this in simple terms."),
        ["chip_summarize"] = ("Resuma", "Summarize"),
        ["chip_summarize_prompt"] = ("Resuma isto em poucos tópicos.", "Summarize this in a few bullet points."),
        ["chip_fix"] = ("Corrija o texto", "Fix the text"),
        ["chip_fix_prompt"] = ("Corrija a ortografia e a gramática do texto e devolva só a versão corrigida.", "Fix the spelling and grammar of the text and return only the corrected version."),
        ["chip_idea"] = ("Me dê ideias", "Give me ideas"),
        ["chip_idea_prompt"] = ("Me dê ideias criativas sobre o que eu disser a seguir.", "Give me creative ideas about what I say next."),
        ["chip_write"] = ("Escreva para mim", "Write for me"),
        ["chip_write_prompt"] = ("Me ajude a escrever um texto curto. Pergunte o assunto se precisar.", "Help me write a short text. Ask for the topic if you need to."),
        ["chip_how"] = ("Como faço…?", "How do I…?"),
        ["chip_how_prompt"] = ("Quero aprender a fazer algo no Windows. Pergunte o que é.", "I want to learn how to do something on Windows. Ask me what it is."),

        // Errors
        ["err_network"] = ("Não foi possível falar com {0}. Verifique sua internet.", "Couldn't reach {0}. Check your internet connection."),
        ["err_local_offline"] = ("A IA local não respondeu em {0}. Abra o Ollama (ou LM Studio) e tente de novo.", "The local AI didn't answer at {0}. Open Ollama (or LM Studio) and try again."),
        ["err_timeout"] = ("{0} demorou demais para responder.", "{0} took too long to answer."),
        ["err_key"] = ("A chave de API do {0} é inválida.", "The {0} API key is invalid."),
        ["err_forbidden"] = ("A chave do {0} não tem permissão para isso.", "The {0} key isn't allowed to do this."),
        ["err_model"] = ("O modelo do {0} não está disponível para esta chave.", "The {0} model isn't available for this key."),
        ["err_local_model"] = ("O modelo \"{0}\" não existe no servidor local. Escolha outro nas configurações.", "The model \"{0}\" doesn't exist on the local server. Pick another in settings."),
        ["err_local_vision"] = ("O modelo \"{0}\" não entende imagens. Escolha um modelo com visão (ex.: gemma4, qwen2.5vl) ou pergunte sem seleção.", "The model \"{0}\" can't read images. Pick a vision model (e.g. gemma4, qwen2.5vl) or ask without a selection."),
        ["err_rate"] = ("{0} está recebendo pedidos demais. Espere um pouco.", "{0} is getting too many requests. Wait a moment."),
        ["err_credit"] = ("Sua conta do {0} está sem créditos.", "Your {0} account is out of credit."),
        ["err_server"] = ("{0} teve um problema no servidor.", "{0} had a server problem."),
        ["err_other"] = ("{0} não conseguiu responder.", "{0} couldn't answer."),

        // Voice
        ["windows_language"] = ("Idioma do Windows", "Windows language"),
        ["swap_languages"] = ("Inverter idiomas", "Swap languages"),
        ["tap_and_speak"] = ("Toque no microfone e fale", "Tap the mic and speak"),
        ["tap_to_continue"] = ("Toque para falar de novo", "Tap to speak again"),
        ["or_type_here"] = ("…ou digite aqui e aperte Enter", "…or type here and press Enter"),
        ["translating"] = ("Traduzindo…", "Translating…"),
        ["didnt_hear"] = ("Não ouvi nada. Toque e tente de novo.", "Didn't hear anything. Tap and try again."),
        ["translate_google"] = ("Traduzir no Google", "Translate with Google"),
        ["speech_privacy"] = ("O reconhecimento de fala online está desligado no Windows.", "Online speech recognition is turned off in Windows."),
        ["open_privacy"] = ("Ativar nas configurações", "Turn it on in settings"),
        ["use_voice_typing"] = ("Usar digitação por voz (Win+H)", "Use voice typing (Win+H)"),
        ["speech_no_mic"] = ("Não consegui usar o microfone. Verifique se apps da área de trabalho podem acessá-lo.", "Couldn't use the microphone. Check that desktop apps can access it."),
        ["open_mic_settings"] = ("Permissões do microfone", "Microphone permissions"),
        ["speech_failed"] = ("O reconhecimento de voz não funcionou aqui.", "Speech recognition didn't work here."),
        ["no_voice"] = ("Não há voz instalada para {0}. Você pode instalar no Windows.", "There's no voice installed for {0}. You can install one in Windows."),
        ["install_voices"] = ("Instalar vozes", "Install voices"),
        ["speech_settings"] = ("Configurações de fala", "Speech settings"),
        ["speak_failed"] = ("Não consegui reproduzir a voz.", "Couldn't play the voice."),

        // Settings shell
        ["settings_title"] = ("Configurações do MouseIA", "MouseIA settings"),
        ["nav_ai"] = ("Inteligência artificial", "Artificial intelligence"),
        ["nav_activation"] = ("Ativação", "Activation"),
        ["nav_voice"] = ("Tradutor e voz", "Translator & voice"),
        ["nav_general"] = ("Geral", "General"),
        ["nav_about"] = ("Sobre", "About"),
        ["sidebar_hint"] = ("Abra o MouseIA com {0} ou balançando o mouse.", "Open MouseIA with {0} or by shaking the mouse."),
        ["save"] = ("Salvar", "Save"),
        ["cancel"] = ("Fechar", "Close"),
        ["saved"] = ("Salvo ✓", "Saved ✓"),
        ["nothing_found"] = ("Nada encontrado", "Nothing found"),
        ["show_hide"] = ("Mostrar/ocultar", "Show/hide"),
        ["optional"] = ("Opcional", "Optional"),
        ["automatic"] = ("Automático", "Automatic"),

        // AI page
        ["ai_subtitle"] = ("Escolha quem responde: uma IA na nuvem com sua chave, ou uma IA rodando no seu PC.", "Choose who answers: a cloud AI with your key, or an AI running on your PC."),
        ["choose_ai"] = ("Provedor", "Provider"),
        ["gemini_help"] = ("Do Google. Chave grátis no AI Studio.", "By Google. Free key from AI Studio."),
        ["openai_help"] = ("Da OpenAI. Cobrança pelo uso.", "By OpenAI. Billed by usage."),
        ["claude_help"] = ("Da Anthropic. Cobrança pelo uso.", "By Anthropic. Billed by usage."),
        ["local_help"] = ("Ollama, LM Studio, llama.cpp… Roda no seu PC e nada sai da sua rede.", "Ollama, LM Studio, llama.cpp… Runs on your PC; nothing leaves your network."),
        ["api_key_for"] = ("Chave de API do {0}", "{0} API key"),
        ["api_key_help"] = ("Fica guardada criptografada no seu usuário do Windows.", "Stored encrypted for your Windows user."),
        ["get_key"] = ("Obter uma chave", "Get a key"),
        ["quality"] = ("Modelo", "Model"),
        ["quality_help"] = ("Rápido responde em segundos. Os outros pensam mais e custam mais. Se o escolhido não estiver disponível, um mais rápido responde.", "Fast answers in seconds. The others think longer and cost more. If the chosen one isn't available, a faster one answers."),
        ["quality_fast"] = ("Rápido", "Fast"),
        ["quality_balanced"] = ("Equilibrado", "Balanced"),
        ["quality_smart"] = ("Mais inteligente", "Smartest"),
        ["test_connection"] = ("Testar conexão", "Test connection"),
        ["testing"] = ("Testando…", "Testing…"),
        ["test_ok"] = ("Funcionando! {0} respondeu em {1}s.", "Working! {0} answered in {1}s."),
        ["test_bad_key"] = ("Chave inválida.", "Invalid key."),
        ["test_no_internet"] = ("Sem conexão com o servidor.", "Can't reach the server."),
        ["test_no_model"] = ("Modelo não encontrado.", "Model not found."),
        ["test_no_credit"] = ("Conta sem créditos.", "Account out of credit."),
        ["test_rate"] = ("Muitos pedidos agora. Tente em instantes.", "Too many requests right now. Try again shortly."),
        ["missing_key"] = ("Cole a chave de API primeiro.", "Paste the API key first."),
        ["local_server"] = ("Servidor", "Server"),
        ["local_server_help"] = ("Endereço compatível com a API da OpenAI. Clique em Detectar para achar sozinho.", "An OpenAI-compatible address. Click Detect to find it automatically."),
        ["detect"] = ("Detectar", "Detect"),
        ["searching"] = ("Procurando…", "Searching…"),
        ["server_found"] = ("{0} encontrado · {1} modelo(s)", "{0} found · {1} model(s)"),
        ["no_local_server"] = ("Nenhum servidor local respondeu. O Ollama/LM Studio está aberto?", "No local server answered. Is Ollama/LM Studio open?"),
        ["this_server"] = ("Este servidor", "This server"),
        ["local_model"] = ("Modelo", "Model"),
        ["local_model_help"] = ("Para perguntar sobre imagens, use um modelo com visão (ex.: gemma4, gemma3, qwen2.5vl, llava).", "To ask about images, use a vision model (e.g. gemma4, gemma3, qwen2.5vl, llava)."),
        ["pick_model"] = ("Escolha um modelo", "Pick a model"),
        ["refresh_models"] = ("Atualizar lista", "Refresh list"),
        ["local_key"] = ("Chave do servidor", "Server key"),
        ["local_key_help"] = ("Só se o seu servidor exigir.", "Only if your server requires one."),
        ["local_howto"] = ("Como começar: instale o Ollama e rode \"ollama pull gemma4:e4b\" no terminal. Depois clique em Detectar.", "Getting started: install Ollama and run \"ollama pull gemma4:e4b\" in a terminal. Then click Detect."),

        // Activation page
        ["activation_subtitle"] = ("Como chamar o MouseIA.", "How to call MouseIA."),
        ["hotkey"] = ("Atalho de teclado", "Keyboard shortcut"),
        ["hotkey_help"] = ("Clique e pressione a nova combinação (ex.: Alt+Shift+M).", "Click and press the new combination (e.g. Alt+Shift+M)."),
        ["press_keys"] = ("Pressione as teclas…", "Press the keys…"),
        ["needs_modifier"] = ("Use Ctrl, Alt ou Win junto", "Use Ctrl, Alt or Win with it"),
        ["shake"] = ("Balançar o mouse", "Shake the mouse"),
        ["shake_help"] = ("Mexa o mouse rápido de um lado para o outro para chamar o MouseIA.", "Move the mouse quickly back and forth to call MouseIA."),
        ["sensitivity"] = ("Sensibilidade", "Sensitivity"),
        ["strong_shake"] = ("Balanço forte", "Strong shake"),
        ["light_shake"] = ("Balanço leve", "Light shake"),
        ["fullscreen_off"] = ("Não balançar em tela cheia", "Ignore shakes in full screen"),
        ["fullscreen_off_help"] = ("Evita abrir sem querer em jogos e vídeos. O atalho continua funcionando.", "Avoids opening by accident in games and videos. The shortcut still works."),
        ["type_to_ask"] = ("Digitar para perguntar", "Type to ask"),
        ["type_to_ask_help"] = ("Com o MouseIA aberto, comece a digitar para perguntar sem selecionar nada.", "With MouseIA open, start typing to ask without selecting anything."),

        // Voice page
        ["voice_subtitle"] = ("Idiomas do tradutor, voz e microfone.", "Translator languages, voice and microphone."),
        ["translate_to_help"] = ("Idioma para onde o botão Traduzir leva o texto.", "The language the Translate button translates into."),
        ["speak_translation"] = ("Ler a tradução em voz alta", "Read translations aloud"),
        ["speak_translation_help"] = ("Fala a tradução assim que ela fica pronta.", "Speaks each translation as soon as it's ready."),
        ["mic_language"] = ("Idioma do microfone", "Microphone language"),
        ["mic_language_help"] = ("Em que idioma você vai falar ao ditar.", "The language you speak when dictating."),
        ["voice_rate"] = ("Velocidade da voz", "Voice speed"),
        ["no_voices"] = ("Nenhuma voz encontrada.", "No voices found."),
        ["voices_installed"] = ("{0} voz(es) instalada(s): {1}", "{0} voice(s) installed: {1}"),
        ["test_voice"] = ("Testar voz", "Test voice"),
        ["voice_sample"] = ("Olá! Eu sou o MouseIA.", "Hi! I'm MouseIA."),

        // General page
        ["general_subtitle"] = ("Idioma, inicialização e leitura automática.", "Language, startup and automatic reading."),
        ["ui_language"] = ("Idioma do aplicativo", "App language"),
        ["ui_language_help"] = ("Também define o idioma padrão das respostas.", "Also sets the default language of answers."),
        ["start_with_windows"] = ("Iniciar com o Windows", "Start with Windows"),
        ["auto_ocr"] = ("Reconhecer texto ao selecionar", "Recognize text on selection"),
        ["auto_ocr_help"] = ("Lê o texto da seleção na hora (offline) para respostas mais precisas.", "Reads the text in the selection right away (offline) for more accurate answers."),
        ["auto_qr"] = ("Procurar QR Codes ao selecionar", "Look for QR codes on selection"),
        ["auto_qr_help"] = ("Avisa quando a seleção tem um QR Code ou código de barras.", "Lets you know when the selection has a QR code or barcode."),
        ["ocr_languages"] = ("Idiomas do reconhecimento de texto do Windows: {0}", "Windows text recognition languages: {0}"),
        ["open_data_folder"] = ("Abrir pasta de dados", "Open data folder"),

        // About page
        ["version"] = ("Versão {0}", "Version {0}"),
        ["about_text"] = ("Selecione qualquer coisa na tela e pergunte à IA, traduza para qualquer idioma (até por voz), copie textos de imagens, vídeos e arquivos e leia QR Codes.", "Select anything on screen and ask AI, translate into any language (even by voice), copy text from images, videos and files, and read QR codes."),
        ["made_by"] = ("Feito por Odorizzi", "Made by Odorizzi"),
        ["credits_title"] = ("Créditos", "Credits"),
        ["credits_text"] = ("Inspirado no Magic Pointer, mod para Windhawk de Rono (licença MIT). Leitura de códigos com ZXing.Net (licença Apache 2.0).", "Inspired by Magic Pointer, a Windhawk mod by Rono (MIT license). Code reading by ZXing.Net (Apache 2.0 license)."),
        ["quit"] = ("Sair do MouseIA", "Quit MouseIA"),

        // Tray
        ["menu_open"] = ("Abrir MouseIA ({0})", "Open MouseIA ({0})"),
        ["menu_settings"] = ("Configurações", "Settings"),
        ["menu_shake_on"] = ("Ativar balançar o mouse", "Turn on mouse shake"),
        ["menu_shake_off"] = ("Desativar balançar o mouse", "Turn off mouse shake"),
        ["menu_quit"] = ("Sair", "Quit"),
        ["welcome_toast"] = ("Pronto! Balance o mouse ou use {0} para selecionar algo na tela.", "Ready! Shake the mouse or press {0} to select something on screen."),
        ["running_toast"] = ("O MouseIA está rodando. Use {0} ou balance o mouse.", "MouseIA is running. Press {0} or shake the mouse."),
        ["hotkey_taken"] = ("O atalho {0} já está em uso por outro programa. Escolha outro nas configurações.", "The shortcut {0} is already used by another program. Pick another in settings."),
    };
}
