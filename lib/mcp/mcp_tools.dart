/// The public MCP names and the private app operations share one catalog.
/// The HTTP Tool Call endpoint uses the same names and argument schemas.
class McpTool {
  const McpTool(
    this.name,
    this.method,
    this.description,
    this.properties, [
    this.required = const [],
  ]);

  final String name;
  final String method;
  final String description;
  final Map<String, Object> properties;
  final List<String> required;

  Map<String, Object> toJson() => {
    'name': name,
    'description': description,
    'inputSchema': {
      'type': 'object',
      'properties': properties,
      'required': required,
      'additionalProperties': false,
    },
  };
}

const _projectId = {'type': 'string', 'description': '课程项目 ID'};
const _cueIndexes = {
  'type': 'array',
  'items': {'type': 'integer'},
  'description': '原始 SRT cue 索引列表',
};
const _wordIndexes = {
  'type': 'array',
  'items': {'type': 'integer'},
  'description': '单词索引列表，不含标点',
};

const mcpTools = <McpTool>[
  McpTool(
    'intensive_listening_status',
    'app.status',
    '制作开始前读取应用与 Agent 会话状态；仅 event=Agent 时可写入。',
    {},
  ),
  McpTool(
    'list_course_projects',
    'projects.list',
    '列出课程项目及制作状态，用于优先恢复已有工程并避免重复创建。',
    {},
  ),
  McpTool(
    'get_course_project',
    'projects.get',
    '读取项目制作状态；长工程可用 fields 与 cueOffset/cueLimit 只取所需数据。',
    {
      'projectId': _projectId,
      'fields': {
        'type': 'array',
        'items': {
          'type': 'string',
          'enum': [
            'examDocument',
            'cues',
            'sections',
            'materials',
            'questions',
            'cloze',
          ],
        },
      },
      'cueOffset': {'type': 'integer', 'minimum': 0},
      'cueLimit': {'type': 'integer', 'minimum': 1, 'maximum': 500},
    },
    ['projectId'],
  ),
  McpTool('create_course_project', 'projects.create', '没有对应工程时创建课程，可同时绑定音频。', {
    'title': {'type': 'string'},
    'audioPath': {'type': 'string', 'description': '本机音频绝对路径'},
  }),
  McpTool(
    'delete_course_project',
    'projects.delete',
    '删除项目及其项目目录。',
    {'projectId': _projectId},
    ['projectId'],
  ),
  McpTool(
    'open_course_project',
    'projects.open',
    '在教师端打开项目。',
    {'projectId': _projectId},
    ['projectId'],
  ),
  McpTool(
    'import_project_media',
    'projects.importMedia',
    '将音频复制到项目。',
    {
      'projectId': _projectId,
      'mediaPath': {'type': 'string'},
    },
    ['projectId', 'mediaPath'],
  ),
  McpTool(
    'import_exam_document',
    'projects.importExamDocument',
    '将 DOCX 试卷复制到项目并提取为 UTF-8 文本，保留段落、表格和自动编号。',
    {
      'projectId': _projectId,
      'documentPath': {'type': 'string', 'description': '本机 DOCX 绝对路径'},
    },
    ['projectId', 'documentPath'],
  ),
  McpTool(
    'read_exam_text',
    'projects.readExamText',
    '读取项目内已经提取的试卷文本；可用 offset/limit 分页。',
    {
      'projectId': _projectId,
      'offset': {'type': 'integer', 'minimum': 0},
      'limit': {'type': 'integer', 'minimum': 1, 'maximum': 50000},
    },
    ['projectId'],
  ),
  McpTool(
    'start_project_asr',
    'projects.startAsr',
    '字幕缺失且尚无转写任务时，将项目加入后台转写队列。',
    {'projectId': _projectId},
    ['projectId'],
  ),
  McpTool(
    'import_project_srt',
    'projects.importSrt',
    '导入 UTF-8 SRT；auto 在时间轴一致时保留题目和有效挖空。',
    {
      'projectId': _projectId,
      'srtPath': {'type': 'string'},
      'mode': {
        'type': 'string',
        'enum': ['auto', 'merge', 'replace'],
        'description': '默认 auto；merge 要求 cue 数量和时间轴一致。',
      },
    },
    ['projectId', 'srtPath'],
  ),
  McpTool(
    'read_project_srt',
    'projects.readSrt',
    '分页读取权威 cue、时间、篇章标记和词索引；按需返回完整 SRT。',
    {
      'projectId': _projectId,
      'offset': {'type': 'integer', 'minimum': 0},
      'limit': {'type': 'integer', 'minimum': 1, 'maximum': 500},
      'includeSrt': {'type': 'boolean'},
    },
    ['projectId'],
  ),
  McpTool(
    'set_cue_text',
    'projects.setCueText',
    '只修改一个 cue 的文本，保留时间轴、题目和仍有效的挖空索引。',
    {
      'projectId': _projectId,
      'cueIndex': {'type': 'integer', 'minimum': 0},
      'text': {'type': 'string'},
    },
    ['projectId', 'cueIndex', 'text'],
  ),
  McpTool(
    'tokenize_lesson_text',
    'text.tokenize',
    '按应用实际规则返回可挖空的英文词和索引。',
    {
      'text': {'type': 'string'},
    },
    ['text'],
  ),
  McpTool(
    'auto_plan_questions',
    'projects.autoPlanQuestions',
    '根据工程 SRT 显式生成可编辑的听力材料与小题初稿；已有题目时保留原内容并返回错误。',
    {'projectId': _projectId},
    ['projectId'],
  ),
  McpTool(
    'apply_question_plan',
    'projects.applyQuestionPlan',
    '按试卷顺序原子替换全部听力材料与小题；同一材料可包含多道小题，cue 不可跨材料重复归属。',
    {
      'projectId': _projectId,
      'materials': {
        'type': 'array',
        'description': '听力材料列表；每段材料共享一组音频 cue，并包含一道或多道小题。',
        'items': {
          'type': 'object',
          'properties': {
            'id': {'type': 'string'},
            'prompt': {'type': 'string', 'description': '听前播报或材料说明'},
            'cueIndexes': _cueIndexes,
            'repeatedCueIndexes': {
              'type': 'array',
              'items': {'type': 'integer'},
              'description': '第二遍朗读的 cue 索引；必须属于本材料的 cueIndexes。',
            },
            'questions': {
              'type': 'array',
              'items': {
                'type': 'object',
                'properties': {
                  'id': {'type': 'string'},
                  'number': {'type': 'integer'},
                  'title': {'type': 'string'},
                },
                'required': ['number', 'title'],
                'additionalProperties': false,
              },
            },
          },
          'required': ['cueIndexes', 'questions'],
          'additionalProperties': false,
        },
      },
      'questions': {
        'type': 'array',
        'description': '兼容旧客户端：每道题独占一段材料；新调用应使用 materials。',
        'items': {
          'type': 'object',
          'properties': {
            'title': {'type': 'string'},
            'cueIndexes': _cueIndexes,
            'id': {'type': 'string'},
          },
          'required': ['title', 'cueIndexes'],
        },
      },
    },
    ['projectId'],
  ),
  McpTool(
    'add_question_group',
    'projects.addGroup',
    '新增一道题；提供 materialId 时加入现有听力材料，否则用 cueIndexes 新建材料。',
    {
      'projectId': _projectId,
      'title': {'type': 'string'},
      'cueIndexes': _cueIndexes,
      'number': {'type': 'integer'},
      'materialId': {'type': 'string'},
      'prompt': {'type': 'string'},
    },
    ['projectId', 'title'],
  ),
  McpTool(
    'edit_question_group',
    'projects.editGroup',
    '编辑题目及字幕范围。',
    {
      'projectId': _projectId,
      'groupId': {'type': 'string'},
      'title': {'type': 'string'},
      'cueIndexes': _cueIndexes,
      'number': {'type': 'integer'},
    },
    ['projectId', 'groupId'],
  ),
  McpTool(
    'delete_question_group',
    'projects.deleteGroup',
    '删除一道题。',
    {
      'projectId': _projectId,
      'groupId': {'type': 'string'},
    },
    ['projectId', 'groupId'],
  ),
  McpTool(
    'apply_cloze_plan',
    'projects.applyClozePlan',
    '使用 read_project_srt 返回的词索引原子替换全部挖空。',
    {
      'projectId': _projectId,
      'items': {
        'type': 'array',
        'items': {
          'type': 'object',
          'properties': {
            'cueIndex': {'type': 'integer'},
            'wordIndexes': _wordIndexes,
          },
          'required': ['cueIndex', 'wordIndexes'],
        },
      },
    },
    ['projectId', 'items'],
  ),
  McpTool(
    'set_sentence_cloze',
    'projects.setCloze',
    '设置句子的挖空词。',
    {
      'projectId': _projectId,
      'cueIndex': {'type': 'integer'},
      'wordIndexes': _wordIndexes,
    },
    ['projectId', 'cueIndex', 'wordIndexes'],
  ),
  McpTool(
    'validate_course_project',
    'projects.validate',
    '交付前检查音频、字幕和课程数据是否完整。',
    {'projectId': _projectId},
    ['projectId'],
  ),
  McpTool(
    'add_project_to_playback',
    'projects.addToLibrary',
    '验证通过后直接加入学生端课程列表，这是课程制作的默认交付方式。',
    {'projectId': _projectId},
    ['projectId'],
  ),
  McpTool(
    'export_ilp',
    'projects.exportIlp',
    '导出 .ilp 精听包。',
    {
      'projectId': _projectId,
      'outputPath': {'type': 'string'},
    },
    ['projectId', 'outputPath'],
  ),
  McpTool(
    'export_standalone_player',
    'projects.exportStandalone',
    '导出包含精简播放器和课程的独立 Windows EXE。',
    {
      'projectId': _projectId,
      'outputPath': {'type': 'string'},
    },
    ['projectId', 'outputPath'],
  ),
];

McpTool? findMcpTool(String name) {
  for (final tool in mcpTools) {
    if (tool.name == name) return tool;
  }
  return null;
}
