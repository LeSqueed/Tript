import { useCallback, useEffect, useState } from 'react';
import type { GameRecordingPromptMessage } from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';
import { useToast } from '../ui/toast/ToastProvider';

export function GameRecordingToasts({ client }: { client: IpcClient }) {
  const { push, dismiss } = useToast();
  const [prompts, setPrompts] = useState<GameRecordingPromptMessage[]>([]);

  useEffect(() => {
    const onPrompt = client.on('gameRecordingPrompt', (content) => {
      const prompt = content as Partial<GameRecordingPromptMessage> | null;
      if (!prompt || typeof prompt.promptId !== 'string' || typeof prompt.gameId !== 'string'
        || typeof prompt.name !== 'string' || typeof prompt.executablePath !== 'string') return;
      setPrompts((current) => current.some((value) => value.promptId === prompt.promptId)
        ? current
        : [...current, prompt as GameRecordingPromptMessage]);
    });
    const onCleared = client.on('gameRecordingPromptCleared', (content) => {
      const value = content as { promptId?: unknown } | null;
      if (typeof value?.promptId === 'string') {
        setPrompts((current) => current.filter((prompt) => prompt.promptId !== value.promptId));
      }
    });
    return () => {
      onPrompt();
      onCleared();
    };
  }, [client]);

  const answer = useCallback((record: boolean) => {
    const prompt = prompts[0];
    if (!prompt) return;
    client.send('GameRecordingConfirm', { promptId: prompt.promptId, record });
    setPrompts((current) => current.filter((value) => value.promptId !== prompt.promptId));
  }, [client, prompts]);

  useEffect(() => {
    const prompt = prompts[0];
    if (!prompt) {
      dismiss('game-recording-prompt');
      return;
    }
    push({
      key: 'game-recording-prompt',
      kind: 'info',
      duration: 0,
      dismissible: false,
      title: `Record ${prompt.name}?`,
      message: `Tript found ${prompt.executablePath} and added it to your games. Automatically record this game?`,
      actions: [
        { label: 'Yes', onClick: () => answer(true) },
        { label: 'No', variant: 'ghost', onClick: () => answer(false) },
      ],
    });
  }, [prompts, push, dismiss, answer]);

  return null;
}
