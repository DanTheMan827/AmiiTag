//
//  AmiiboFilePicker.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 2/16/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import MobileCoreServices

class AmiiboFilePicker: UIDocumentPickerViewController, UIDocumentPickerDelegate {
    var completionHandler: ((Result<TagDump, Error>) -> Void)? = nil
    public func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else {
            return
        }
        if url.isFileURL {
            let shouldStopAccessing = url.startAccessingSecurityScopedResource()
            let path = url.path
            if let attr = try? FileManager.default.attributesOfItem(atPath: path) as NSDictionary {
                if attr.fileSize() == 532 || attr.fileSize() == 540 || attr.fileSize() == 572 {
                    if let data = try? Data(contentsOf: url),
                       let dump = TagDump(data: data) {
                        dismiss(animated: true) {
                            self.completionHandler?(.success(dump))
                        }
                    } else {
                        self.completionHandler?(.failure(AmiiTagError(description: "Unable to open file")))
                    }
                    
                } else {
                    self.completionHandler?(.failure(AmiiTagError(description: "Selected file is not valid")))
                }
            }
            if shouldStopAccessing {
                url.stopAccessingSecurityScopedResource()
            }
        }
    }
    
    fileprivate override init(documentTypes allowedUTIs: [String], in mode: UIDocumentPickerMode) {
        super.init(documentTypes: allowedUTIs, in: mode)
    }
    
    required init?(coder: NSCoder) {
        super.init(coder: coder)
    }
    
    static func OpenAmiibo(PresentingViewController presentingVc: UIViewController, completionHandler: @escaping (Result<TagDump, Error>) -> Void){
        var pickerController = AmiiboFilePicker(documentTypes: [kUTTypeData as String], in: .open)
        pickerController.completionHandler = completionHandler
        pickerController.delegate = pickerController
        pickerController.allowsMultipleSelection = false
        presentingVc.present(pickerController, animated: true, completion: nil)
    }
}
