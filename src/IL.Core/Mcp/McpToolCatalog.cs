using System.Text.Json.Nodes;
namespace IL.Core.Mcp;
public static class McpToolCatalog
{
    private const string CatalogJson = """
[
  {
    "name": "register_agent",
    "method": "agent.register",
    "description": "首次连接时提交自报名称，由应用分配 UUID。请妥善保存返回的 MCP URL。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "agentName": {
          "type": "string",
          "description": "智能体自报名称"
        }
      },
      "required": [
        "agentName"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "change_event",
    "method": "agent.changeEvent",
    "description": "请求接管或结束接管；Agent 需要应用内首次审批。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "event": {
          "type": "string",
          "enum": [
            "Agent",
            "User"
          ]
        },
        "agentUuid": {
          "type": "string",
          "description": "register_agent 分配的 UUID"
        }
      },
      "required": [
        "event"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "end_agent_session",
    "method": "agent.disconnect",
    "description": "制作完成后结束本次接管并返回 User 模式。",
    "inputSchema": {
      "type": "object",
      "properties": {},
      "required": [],
      "additionalProperties": false
    }
  },
  {
    "name": "report_authoring_step",
    "method": "agent.reportStep",
    "description": "上报制作 SKILL.md 中第 1 至 11 步的状态与详细说明，更新应用接管界面的步骤列表和进度条；并行步骤可分别上报。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "step": { "type": "integer", "minimum": 1, "maximum": 11 },
        "status": { "type": "string", "enum": ["running", "completed", "skipped", "failed"] },
        "detail": { "type": "string", "maxLength": 1000, "description": "当前操作、完成结果、跳过原因或失败原因，显示给用户" }
      },
      "required": ["step", "status"],
      "additionalProperties": false
    }
  },
  {
    "name": "intensive_listening_status",
    "method": "app.status",
    "description": "制作开始前读取应用与 Agent 会话状态；仅 event=Agent 时可写入。",
    "inputSchema": {
      "type": "object",
      "properties": {},
      "required": [],
      "additionalProperties": false
    }
  },
  {
    "name": "list_course_projects",
    "method": "projects.list",
    "description": "列出课程项目及制作状态，用于优先恢复已有工程并避免重复创建。",
    "inputSchema": {
      "type": "object",
      "properties": {},
      "required": [],
      "additionalProperties": false
    }
  },
  {
    "name": "get_course_project",
    "method": "projects.get",
    "description": "读取项目制作状态；长工程可用 fields 与 cueOffset/cueLimit 只取所需数据。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "fields": {
          "type": "array",
          "items": {
            "type": "string",
            "enum": [
              "examDocument",
              "cues",
              "sections",
              "materials",
              "questions",
              "cloze"
            ]
          }
        },
        "cueOffset": {
          "type": "integer",
          "minimum": 0
        },
        "cueLimit": {
          "type": "integer",
          "minimum": 1,
          "maximum": 500
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "create_course_project",
    "method": "projects.create",
    "description": "没有对应工程时创建课程，可同时绑定音频。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "title": {
          "type": "string"
        },
        "audioPath": {
          "type": "string",
          "description": "本机音频绝对路径"
        }
      },
      "required": [],
      "additionalProperties": false
    }
  },
  {
    "name": "delete_course_project",
    "method": "projects.delete",
    "description": "删除项目及其项目目录。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "open_course_project",
    "method": "projects.open",
    "description": "在教师端打开项目。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "import_project_media",
    "method": "projects.importMedia",
    "description": "将音频复制到项目。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "mediaPath": {
          "type": "string"
        }
      },
      "required": [
        "projectId",
        "mediaPath"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "import_exam_document",
    "method": "projects.importExamDocument",
    "description": "将 DOCX 试卷复制到项目并提取为 UTF-8 文本，保留段落、表格和自动编号。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "documentPath": {
          "type": "string",
          "description": "本机 DOCX 绝对路径"
        }
      },
      "required": [
        "projectId",
        "documentPath"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "read_exam_text",
    "method": "projects.readExamText",
    "description": "读取项目内已经提取的试卷文本；可用 offset/limit 分页。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "offset": {
          "type": "integer",
          "minimum": 0
        },
        "limit": {
          "type": "integer",
          "minimum": 1,
          "maximum": 50000
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "import_project_text",
    "method": "projects.importText",
    "description": "导入 Agent 在本机从 DOCX 或可复制 PDF 提取的 UTF-8 文本；role 为 exam 或 reference。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "role": {
          "type": "string",
          "enum": [
            "exam",
            "reference"
          ]
        },
        "textPath": {
          "type": "string"
        },
        "sourcePath": {
          "type": "string"
        }
      },
      "required": [
        "projectId",
        "role",
        "textPath"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "read_project_text",
    "method": "projects.readText",
    "description": "分页读取工程内的试卷或答案/听力原文文本。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "role": {
          "type": "string",
          "enum": [
            "exam",
            "reference"
          ]
        },
        "offset": {
          "type": "integer",
          "minimum": 0
        },
        "limit": {
          "type": "integer",
          "minimum": 1,
          "maximum": 50000
        }
      },
      "required": [
        "projectId",
        "role"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "start_project_asr",
    "method": "projects.startAsr",
    "description": "字幕缺失且尚无转写任务时，将项目加入后台转写队列。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "import_project_srt",
    "method": "projects.importSrt",
    "description": "导入 UTF-8 SRT；auto 在时间轴一致时保留题目和有效挖空。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "srtPath": {
          "type": "string"
        },
        "mode": {
          "type": "string",
          "enum": [
            "auto",
            "merge",
            "replace"
          ],
          "description": "默认 auto；merge 要求 cue 数量和时间轴一致。"
        }
      },
      "required": [
        "projectId",
        "srtPath"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "read_project_srt",
    "method": "projects.readSrt",
    "description": "分页读取权威 cue、时间、篇章标记和词索引；按需返回完整 SRT。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "offset": {
          "type": "integer",
          "minimum": 0
        },
        "limit": {
          "type": "integer",
          "minimum": 1,
          "maximum": 500
        },
        "includeSrt": {
          "type": "boolean"
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "set_cue_text",
    "method": "projects.setCueText",
    "description": "只修改一个 cue 的文本，保留时间轴、题目和仍有效的挖空索引。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "cueIndex": {
          "type": "integer",
          "minimum": 0
        },
        "text": {
          "type": "string"
        }
      },
      "required": [
        "projectId",
        "cueIndex",
        "text"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "tokenize_lesson_text",
    "method": "text.tokenize",
    "description": "按应用实际规则返回可挖空的英文词和索引。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "text": {
          "type": "string"
        }
      },
      "required": [
        "text"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "auto_plan_questions",
    "method": "projects.autoPlanQuestions",
    "description": "根据工程 SRT 显式生成可编辑的听力材料与小题初稿；已有题目时保留原内容并返回错误。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "apply_question_plan",
    "method": "projects.applyQuestionPlan",
    "description": "按试卷顺序原子替换全部听力材料与小题；同一材料可包含多道小题，cue 不可跨材料重复归属。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "materials": {
          "type": "array",
          "description": "听力材料列表；每段材料共享一组音频 cue，并包含一道或多道小题。",
          "items": {
            "type": "object",
            "properties": {
              "id": {
                "type": "string"
              },
              "prompt": {
                "type": "string",
                "description": "听前播报或材料说明"
              },
              "cueIndexes": {
                "type": "array",
                "items": {
                  "type": "integer"
                },
                "description": "原始 SRT cue 索引列表"
              },
              "repeatedCueIndexes": {
                "type": "array",
                "items": {
                  "type": "integer"
                },
                "description": "第二遍朗读的 cue 索引；必须属于本材料的 cueIndexes。"
              },
              "leadInCueIndexes": {
                "type": "array",
                "items": {
                  "type": "integer"
                },
                "description": "原始 SRT cue 索引列表"
              },
              "questions": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "id": {
                      "type": "string"
                    },
                    "number": {
                      "type": "integer"
                    },
                    "title": {
                      "type": "string"
                    },
                    "options": {
                      "type": "array",
                      "items": {
                        "type": "string"
                      }
                    },
                    "answerIndex": {
                      "type": "integer",
                      "minimum": 0
                    }
                  },
                  "required": [
                    "number",
                    "title"
                  ],
                  "additionalProperties": false
                }
              }
            },
            "required": [
              "cueIndexes",
              "questions"
            ],
            "additionalProperties": false
          }
        },
        "questions": {
          "type": "array",
          "description": "兼容旧客户端：每道题独占一段材料；新调用应使用 materials。",
          "items": {
            "type": "object",
            "properties": {
              "title": {
                "type": "string"
              },
              "cueIndexes": {
                "type": "array",
                "items": {
                  "type": "integer"
                },
                "description": "原始 SRT cue 索引列表"
              },
              "id": {
                "type": "string"
              }
            },
            "required": [
              "title",
              "cueIndexes"
            ]
          }
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "add_question_group",
    "method": "projects.addGroup",
    "description": "新增一道题；提供 materialId 时加入现有听力材料，否则用 cueIndexes 新建材料。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "title": {
          "type": "string"
        },
        "cueIndexes": {
          "type": "array",
          "items": {
            "type": "integer"
          },
          "description": "原始 SRT cue 索引列表"
        },
        "number": {
          "type": "integer"
        },
        "materialId": {
          "type": "string"
        },
        "prompt": {
          "type": "string"
        },
        "leadInCueIndexes": {
          "type": "array",
          "items": {
            "type": "integer"
          },
          "description": "原始 SRT cue 索引列表"
        },
        "options": {
          "type": "array",
          "items": {
            "type": "string"
          }
        },
        "answerIndex": {
          "type": "integer",
          "minimum": 0
        }
      },
      "required": [
        "projectId",
        "title"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "edit_question_group",
    "method": "projects.editGroup",
    "description": "编辑题目及字幕范围。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "groupId": {
          "type": "string"
        },
        "title": {
          "type": "string"
        },
        "cueIndexes": {
          "type": "array",
          "items": {
            "type": "integer"
          },
          "description": "原始 SRT cue 索引列表"
        },
        "number": {
          "type": "integer"
        },
        "options": {
          "type": "array",
          "items": {
            "type": "string"
          }
        },
        "answerIndex": {
          "type": "integer",
          "minimum": 0
        },
        "leadInCueIndexes": {
          "type": "array",
          "items": {
            "type": "integer"
          },
          "description": "原始 SRT cue 索引列表"
        }
      },
      "required": [
        "projectId",
        "groupId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "delete_question_group",
    "method": "projects.deleteGroup",
    "description": "删除一道题。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "groupId": {
          "type": "string"
        }
      },
      "required": [
        "projectId",
        "groupId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "apply_cloze_plan",
    "method": "projects.applyClozePlan",
    "description": "使用 read_project_srt 返回的词索引原子替换全部挖空。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "items": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "cueIndex": {
                "type": "integer"
              },
              "wordIndexes": {
                "type": "array",
                "items": {
                  "type": "integer"
                },
                "description": "单词索引列表，不含标点"
              }
            },
            "required": [
              "cueIndex",
              "wordIndexes"
            ]
          }
        }
      },
      "required": [
        "projectId",
        "items"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "set_sentence_cloze",
    "method": "projects.setCloze",
    "description": "设置句子的挖空词。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "cueIndex": {
          "type": "integer"
        },
        "wordIndexes": {
          "type": "array",
          "items": {
            "type": "integer"
          },
          "description": "单词索引列表，不含标点"
        }
      },
      "required": [
        "projectId",
        "cueIndex",
        "wordIndexes"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "validate_course_project",
    "method": "projects.validate",
    "description": "交付前检查音频、字幕和课程数据是否完整。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "add_project_to_playback",
    "method": "projects.addToLibrary",
    "description": "验证通过后直接加入学生端课程列表，这是课程制作的默认交付方式。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        }
      },
      "required": [
        "projectId"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "export_ilp",
    "method": "projects.exportIlp",
    "description": "导出 .ilp 精听包。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "outputPath": {
          "type": "string"
        }
      },
      "required": [
        "projectId",
        "outputPath"
      ],
      "additionalProperties": false
    }
  },
  {
    "name": "export_standalone_player",
    "method": "projects.exportStandalone",
    "description": "导出包含精简播放器和课程的独立 Windows EXE。",
    "inputSchema": {
      "type": "object",
      "properties": {
        "projectId": {
          "type": "string",
          "description": "课程项目 ID"
        },
        "outputPath": {
          "type": "string"
        }
      },
      "required": [
        "projectId",
        "outputPath"
      ],
      "additionalProperties": false
    }
  }
]
""";
    public static JsonArray Tools => JsonNode.Parse(CatalogJson)!.AsArray();
    public static JsonObject? Find(string name) => Tools.OfType<JsonObject>().FirstOrDefault(x=>x["name"]?.ToString()==name);
    public static JsonArray PublicTools => new(Tools.OfType<JsonObject>().Select(x => { var tool=(JsonObject)x.DeepClone(); tool.Remove("method"); return (JsonNode)tool; }).ToArray());
}
